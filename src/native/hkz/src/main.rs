use anyhow::{Context, Result, anyhow, bail, ensure};
use ignore::{DirEntry, WalkBuilder, WalkState};
use rayon::prelude::*;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::env;
use std::ffi::{OsStr, OsString};
use std::fs;
use std::path::{Path, PathBuf};
use std::process::{self, Command, ExitStatus, Stdio};
use std::sync::{Arc, Mutex};

const CONFIG_DIR_NAME: &str = ".hkz.build";
const CONFIG_FILE_NAME: &str = "config.toml";
const CACHE_FILE_NAME: &str = "cache.toml";
const SCHEMA_VERSION: u32 = 1;

fn main() {
    match run() {
        Ok(code) => process::exit(code),
        Err(error) => {
            eprintln!("{error:#}");
            process::exit(1);
        }
    }
}

fn run() -> Result<i32> {
    let forwarded_args = env::args_os().skip(1).collect::<Vec<_>>();
    let workspace =
        find_workspace(&env::current_dir().context("failed to determine current directory")?)?;
    let config = load_config(&workspace)?;
    let cache = load_cache(&workspace.cache_path)?;
    let source_files = collect_source_files(&workspace.root, &config.projects)?;
    let file_hashes = hash_source_files(&source_files)?;

    let mut target_dll = query_target_dll(&config.main_project)?;
    let needs_rebuild = should_rebuild(cache.as_ref(), &file_hashes, &target_dll);

    if needs_rebuild {
        run_dotnet_build(&config.main_project)?;
        target_dll = query_target_dll(&config.main_project)?;
        ensure!(
            target_dll.is_file(),
            "dotnet build succeeded but target DLL does not exist: {}",
            target_dll.display()
        );
        write_cache(
            &workspace.cache_path,
            &workspace.root,
            &target_dll,
            &file_hashes,
        )?;
    }

    run_dotnet_exec(&target_dll, &forwarded_args)
}

#[derive(Debug)]
struct WorkspacePaths {
    root: PathBuf,
    cache_path: PathBuf,
    config_path: PathBuf,
}

#[derive(Debug, Deserialize)]
struct ConfigFile {
    version: u32,
    main_project: String,
    projects: Vec<String>,
}

#[derive(Debug)]
struct ResolvedConfig {
    main_project: PathBuf,
    projects: Vec<PathBuf>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
struct CacheFile {
    version: u32,
    target_dll: String,
    #[serde(default)]
    files: BTreeMap<String, String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct SourceFile {
    relative_path: String,
    absolute_path: PathBuf,
}

fn find_workspace(start: &Path) -> Result<WorkspacePaths> {
    let config_dir = start
        .ancestors()
        .map(|ancestor| ancestor.join(CONFIG_DIR_NAME))
        .find(|candidate| candidate.is_dir())
        .with_context(|| {
            format!(
                "could not find `{CONFIG_DIR_NAME}` starting from {}",
                start.display()
            )
        })?;

    let config_dir = dunce::canonicalize(&config_dir)
        .with_context(|| format!("failed to canonicalize {}", config_dir.display()))?;
    let root = config_dir
        .parent()
        .map(Path::to_path_buf)
        .ok_or_else(|| anyhow!("`{CONFIG_DIR_NAME}` must have a parent directory"))?;

    Ok(WorkspacePaths {
        cache_path: config_dir.join(CACHE_FILE_NAME),
        config_path: config_dir.join(CONFIG_FILE_NAME),
        root,
    })
}

fn load_config(workspace: &WorkspacePaths) -> Result<ResolvedConfig> {
    let config_text = fs::read_to_string(&workspace.config_path)
        .with_context(|| format!("failed to read {}", workspace.config_path.display()))?;
    let config: ConfigFile = toml::from_str(&config_text)
        .with_context(|| format!("failed to parse {}", workspace.config_path.display()))?;

    validate_config(config, &workspace.root)
}

fn validate_config(config: ConfigFile, workspace_root: &Path) -> Result<ResolvedConfig> {
    ensure!(
        config.version == SCHEMA_VERSION,
        "unsupported config version {}, expected {}",
        config.version,
        SCHEMA_VERSION
    );
    ensure!(
        !config.projects.is_empty(),
        "`projects` must contain at least one C# project"
    );

    let main_project = resolve_project_path(workspace_root, &config.main_project)
        .context("failed to resolve `main_project`")?;
    let mut projects = Vec::with_capacity(config.projects.len());

    for project in &config.projects {
        projects.push(
            resolve_project_path(workspace_root, project)
                .with_context(|| format!("failed to resolve project `{project}`"))?,
        );
    }

    ensure!(
        projects.iter().any(|project| project == &main_project),
        "`main_project` must also appear in `projects`"
    );

    Ok(ResolvedConfig {
        main_project,
        projects,
    })
}

fn resolve_project_path(workspace_root: &Path, configured_path: &str) -> Result<PathBuf> {
    let relative_path = Path::new(configured_path);
    ensure!(
        !relative_path.is_absolute(),
        "project path must be relative to workspace root: {configured_path}"
    );
    ensure!(
        relative_path.extension() == Some(OsStr::new("csproj")),
        "project path must point to a .csproj file: {configured_path}"
    );

    let absolute_path = workspace_root.join(relative_path);
    ensure!(
        absolute_path.is_file(),
        "project file does not exist: {}",
        absolute_path.display()
    );

    dunce::canonicalize(&absolute_path)
        .with_context(|| format!("failed to canonicalize {}", absolute_path.display()))
}

fn load_cache(cache_path: &Path) -> Result<Option<CacheFile>> {
    if !cache_path.exists() {
        return Ok(None);
    }

    let cache_text = fs::read_to_string(cache_path)
        .with_context(|| format!("failed to read {}", cache_path.display()))?;
    let cache: CacheFile = toml::from_str(&cache_text)
        .with_context(|| format!("failed to parse {}", cache_path.display()))?;

    ensure!(
        cache.version == SCHEMA_VERSION,
        "unsupported cache version {}, expected {}",
        cache.version,
        SCHEMA_VERSION
    );

    Ok(Some(cache))
}

fn collect_source_files(workspace_root: &Path, projects: &[PathBuf]) -> Result<Vec<SourceFile>> {
    let files = Arc::new(Mutex::new(BTreeMap::<String, PathBuf>::new()));
    let first_error = Arc::new(Mutex::new(None::<anyhow::Error>));
    let workspace_root = Arc::new(workspace_root.to_path_buf());

    for project in projects {
        let project_dir = project
            .parent()
            .map(Path::to_path_buf)
            .ok_or_else(|| anyhow!("project path has no parent: {}", project.display()))?;
        let files = Arc::clone(&files);
        let first_error = Arc::clone(&first_error);
        let workspace_root = Arc::clone(&workspace_root);

        WalkBuilder::new(&project_dir)
            .hidden(false)
            .ignore(false)
            .git_ignore(false)
            .git_global(false)
            .git_exclude(false)
            .parents(false)
            .threads(0)
            .build_parallel()
            .run(|| {
                let files = Arc::clone(&files);
                let first_error = Arc::clone(&first_error);
                let workspace_root = Arc::clone(&workspace_root);

                Box::new(move |entry| {
                    if first_error.lock().expect("mutex poisoned").is_some() {
                        return WalkState::Quit;
                    }

                    match entry {
                        Ok(entry) => match handle_walk_entry(&workspace_root, entry, &files) {
                            Ok(state) => state,
                            Err(error) => {
                                *first_error.lock().expect("mutex poisoned") = Some(error);
                                WalkState::Quit
                            }
                        },
                        Err(error) => {
                            *first_error.lock().expect("mutex poisoned") = Some(anyhow!(error));
                            WalkState::Quit
                        }
                    }
                })
            });
    }

    if let Some(error) = first_error.lock().expect("mutex poisoned").take() {
        return Err(error);
    }

    let files = Arc::try_unwrap(files)
        .expect("all walker handles should be dropped")
        .into_inner()
        .expect("mutex poisoned");

    Ok(files
        .into_iter()
        .map(|(relative_path, absolute_path)| SourceFile {
            relative_path,
            absolute_path,
        })
        .collect())
}

fn handle_walk_entry(
    workspace_root: &Path,
    entry: DirEntry,
    files: &Arc<Mutex<BTreeMap<String, PathBuf>>>,
) -> Result<WalkState> {
    let file_type = match entry.file_type() {
        Some(file_type) => file_type,
        None => return Ok(WalkState::Continue),
    };

    if file_type.is_dir() && is_excluded_directory(entry.path()) {
        return Ok(WalkState::Skip);
    }

    if !file_type.is_file() || entry.path().extension() != Some(OsStr::new("cs")) {
        return Ok(WalkState::Continue);
    }

    let absolute_path = entry.path().to_path_buf();
    let relative_path = relative_path_string(workspace_root, &absolute_path)?;

    files
        .lock()
        .expect("mutex poisoned")
        .entry(relative_path)
        .or_insert(absolute_path);

    Ok(WalkState::Continue)
}

fn is_excluded_directory(path: &Path) -> bool {
    matches!(
        path.file_name().and_then(OsStr::to_str),
        Some("bin" | "obj" | ".git" | ".vs")
    )
}

fn relative_path_string(root: &Path, path: &Path) -> Result<String> {
    let relative = path
        .strip_prefix(root)
        .with_context(|| format!("{} is not inside {}", path.display(), root.display()))?;
    Ok(path_to_slash_string(relative))
}

fn path_to_slash_string(path: &Path) -> String {
    path.components()
        .map(|component| component.as_os_str().to_string_lossy().into_owned())
        .collect::<Vec<_>>()
        .join("/")
}

fn hash_source_files(files: &[SourceFile]) -> Result<BTreeMap<String, String>> {
    let hashed_files = files
        .par_iter()
        .map(|source_file| -> Result<(String, String)> {
            let bytes = fs::read(&source_file.absolute_path).with_context(|| {
                format!(
                    "failed to read source file {}",
                    source_file.absolute_path.display()
                )
            })?;
            let hash = blake3::hash(&bytes).to_hex().to_string();
            Ok((source_file.relative_path.clone(), hash))
        })
        .collect::<Result<Vec<_>>>()?;

    Ok(hashed_files.into_iter().collect())
}

fn should_rebuild(
    cache: Option<&CacheFile>,
    current_hashes: &BTreeMap<String, String>,
    target_dll: &Path,
) -> bool {
    if !target_dll.is_file() {
        return true;
    }

    match cache {
        Some(cache) => cache.files != *current_hashes,
        None => true,
    }
}

fn query_target_dll(main_project: &Path) -> Result<PathBuf> {
    let output = Command::new("dotnet")
        .arg("msbuild")
        .arg(main_project)
        .arg("-p:Configuration=Release")
        .arg("-getProperty:TargetPath")
        .output()
        .with_context(|| format!("failed to query target path for {}", main_project.display()))?;

    if !output.status.success() {
        bail!(
            "dotnet msbuild failed for {}:\nstdout:\n{}\nstderr:\n{}",
            main_project.display(),
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        );
    }

    let stdout = String::from_utf8_lossy(&output.stdout);
    let path = stdout
        .lines()
        .rev()
        .find(|line| !line.trim().is_empty())
        .map(str::trim)
        .ok_or_else(|| anyhow!("dotnet msbuild did not return a target path"))?;

    Ok(PathBuf::from(path))
}

fn run_dotnet_build(main_project: &Path) -> Result<()> {
    let status = Command::new("dotnet")
        .arg("build")
        .arg(main_project)
        .arg("-c")
        .arg("Release")
        .stdin(Stdio::inherit())
        .stdout(Stdio::inherit())
        .stderr(Stdio::inherit())
        .status()
        .with_context(|| format!("failed to run dotnet build for {}", main_project.display()))?;

    ensure!(
        status.success(),
        "dotnet build failed for {} with status {}",
        main_project.display(),
        status
    );

    Ok(())
}

fn write_cache(
    cache_path: &Path,
    workspace_root: &Path,
    target_dll: &Path,
    file_hashes: &BTreeMap<String, String>,
) -> Result<()> {
    let cache = CacheFile {
        version: SCHEMA_VERSION,
        target_dll: relative_path_string(workspace_root, target_dll)?,
        files: file_hashes.clone(),
    };
    let cache_text = toml::to_string_pretty(&cache).context("failed to serialize cache")?;
    fs::write(cache_path, format!("{cache_text}\n"))
        .with_context(|| format!("failed to write {}", cache_path.display()))
}

fn run_dotnet_exec(target_dll: &Path, forwarded_args: &[OsString]) -> Result<i32> {
    let mut command = Command::new("dotnet");
    command
        .args(dotnet_exec_arguments(target_dll, forwarded_args))
        .stdin(Stdio::inherit())
        .stdout(Stdio::inherit())
        .stderr(Stdio::inherit());

    let status = command
        .status()
        .with_context(|| format!("failed to run dotnet exec for {}", target_dll.display()))?;

    exit_code_from_status(status)
}

fn dotnet_exec_arguments(target_dll: &Path, forwarded_args: &[OsString]) -> Vec<OsString> {
    let mut args = vec![
        OsString::from("exec"),
        target_dll.as_os_str().to_os_string(),
    ];
    args.extend(forwarded_args.iter().cloned());
    args
}

fn exit_code_from_status(status: ExitStatus) -> Result<i32> {
    status
        .code()
        .ok_or_else(|| anyhow!("child process terminated without an exit code"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::TempDir;

    #[cfg(unix)]
    use std::os::unix::process::ExitStatusExt;
    #[cfg(windows)]
    use std::os::windows::process::ExitStatusExt;

    #[test]
    fn finds_nearest_workspace_from_nested_directory() -> Result<()> {
        let temp_dir = TempDir::new()?;
        let workspace_root = temp_dir.path().join("repo");
        let nested_dir = workspace_root.join("a/b/c");

        fs::create_dir_all(workspace_root.join(CONFIG_DIR_NAME))?;
        fs::create_dir_all(&nested_dir)?;
        let expected_root = dunce::canonicalize(&workspace_root)?;

        let workspace = find_workspace(&nested_dir)?;

        assert_eq!(workspace.root, expected_root);
        assert_eq!(
            workspace.config_path,
            expected_root.join(CONFIG_DIR_NAME).join(CONFIG_FILE_NAME)
        );
        assert_eq!(
            workspace.cache_path,
            expected_root.join(CONFIG_DIR_NAME).join(CACHE_FILE_NAME)
        );

        Ok(())
    }

    #[test]
    fn validate_config_rejects_invalid_inputs() -> Result<()> {
        let temp_dir = TempDir::new()?;
        let workspace_root = temp_dir.path();
        let project_path = workspace_root.join("src/App/App.csproj");
        write_file(&project_path, "<Project />")?;

        let missing_main = ConfigFile {
            version: SCHEMA_VERSION,
            main_project: "src/App/App.csproj".into(),
            projects: vec!["src/Other/Other.csproj".into()],
        };
        assert!(validate_config(missing_main, workspace_root).is_err());

        let wrong_version = ConfigFile {
            version: 2,
            main_project: "src/App/App.csproj".into(),
            projects: vec!["src/App/App.csproj".into()],
        };
        assert!(validate_config(wrong_version, workspace_root).is_err());

        let missing_project = ConfigFile {
            version: SCHEMA_VERSION,
            main_project: "src/Missing/Missing.csproj".into(),
            projects: vec!["src/Missing/Missing.csproj".into()],
        };
        assert!(validate_config(missing_project, workspace_root).is_err());

        Ok(())
    }

    #[test]
    fn collect_source_files_skips_build_outputs_and_deduplicates() -> Result<()> {
        let temp_dir = TempDir::new()?;
        let workspace_root = temp_dir.path();
        let project_a = workspace_root.join("src/App/App.csproj");
        let project_b = workspace_root.join("src/App/Sub/Sub.csproj");

        write_file(&project_a, "<Project />")?;
        write_file(&project_b, "<Project />")?;
        write_file(
            &workspace_root.join("src/App/Program.cs"),
            "class Program {}",
        )?;
        write_file(
            &workspace_root.join("src/App/Sub/Feature.cs"),
            "class Feature {}",
        )?;
        write_file(
            &workspace_root.join("src/App/bin/Generated.cs"),
            "class Ignored {}",
        )?;
        write_file(
            &workspace_root.join("src/App/obj/Generated.cs"),
            "class Ignored {}",
        )?;

        let files = collect_source_files(workspace_root, &[project_a, project_b])?;
        let paths = files
            .into_iter()
            .map(|file| file.relative_path)
            .collect::<Vec<_>>();

        assert_eq!(
            paths,
            vec![
                "src/App/Program.cs".to_string(),
                "src/App/Sub/Feature.cs".to_string()
            ]
        );

        Ok(())
    }

    #[test]
    fn rebuild_detection_handles_cache_dll_and_hash_changes() {
        let hashes = BTreeMap::from([("src/App/Program.cs".to_string(), "hash-a".to_string())]);
        let cache = CacheFile {
            version: SCHEMA_VERSION,
            target_dll: "src/App/bin/Release/net10.0/App.dll".into(),
            files: hashes.clone(),
        };
        let missing_dll = PathBuf::from("/tmp/missing.dll");

        assert!(should_rebuild(None, &hashes, &missing_dll));

        let existing_temp = TempDir::new().expect("temp dir");
        let existing_dll = existing_temp.path().join("App.dll");
        fs::write(&existing_dll, []).expect("write dll");

        assert!(!should_rebuild(Some(&cache), &hashes, &existing_dll));

        let changed_hashes =
            BTreeMap::from([("src/App/Program.cs".to_string(), "hash-b".to_string())]);
        assert!(should_rebuild(Some(&cache), &changed_hashes, &existing_dll));

        let extra_hashes = BTreeMap::from([
            ("src/App/Feature.cs".to_string(), "hash-c".to_string()),
            ("src/App/Program.cs".to_string(), "hash-a".to_string()),
        ]);
        assert!(should_rebuild(Some(&cache), &extra_hashes, &existing_dll));
    }

    #[test]
    fn write_cache_uses_stable_relative_paths() -> Result<()> {
        let temp_dir = TempDir::new()?;
        let workspace_root = temp_dir.path();
        let cache_path = workspace_root.join(CONFIG_DIR_NAME).join(CACHE_FILE_NAME);
        let target_dll = workspace_root.join("src/App/bin/Release/net10.0/App.dll");

        fs::create_dir_all(cache_path.parent().expect("cache parent"))?;
        write_file(&target_dll, "")?;

        let file_hashes = BTreeMap::from([
            ("src/App/A.cs".to_string(), "aaa".to_string()),
            ("src/App/B.cs".to_string(), "bbb".to_string()),
        ]);

        write_cache(&cache_path, workspace_root, &target_dll, &file_hashes)?;
        let content = fs::read_to_string(&cache_path)?;

        assert!(content.contains("target_dll = \"src/App/bin/Release/net10.0/App.dll\""));
        assert!(content.contains("\"src/App/A.cs\" = \"aaa\""));
        assert!(content.contains("\"src/App/B.cs\" = \"bbb\""));
        assert!(content.find("A.cs").unwrap() < content.find("B.cs").unwrap());

        Ok(())
    }

    #[test]
    fn dotnet_exec_arguments_forward_all_args() {
        let args = dotnet_exec_arguments(
            Path::new("/tmp/App.dll"),
            &[OsString::from("a"), OsString::from("--flag")],
        );

        assert_eq!(
            args,
            vec![
                OsString::from("exec"),
                OsString::from("/tmp/App.dll"),
                OsString::from("a"),
                OsString::from("--flag"),
            ]
        );
    }

    #[test]
    fn exit_code_is_forwarded() -> Result<()> {
        #[cfg(unix)]
        let status = ExitStatus::from_raw(7 << 8);
        #[cfg(windows)]
        let status = ExitStatus::from_raw(7);

        assert_eq!(exit_code_from_status(status)?, 7);
        Ok(())
    }

    fn write_file(path: &Path, contents: &str) -> Result<()> {
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        fs::write(path, contents)?;
        Ok(())
    }
}
