using System.Collections.Immutable;
using Hakaze.Build.Abstractions;
using Hakaze.Build.Core;

namespace Hakaze.Build.Core.Tests;

public class BuildingBuilderTests
{
    [Test]
    public async Task ProfileBuilder_BuildsExpectedProfile()
    {
        var profile = new ProfileBuilder()
                      .WithName("Release")
                      .SetOptimizeCode()
                      .Build();

        await Assert.That(profile.Name).IsEqualTo("Release");
        await Assert.That(profile.OptimizeCode).IsTrue();
        await Assert.That(profile.GenerateDebugInfo).IsFalse();
    }

    [Test]
    public async Task ProfileExtensions_IsReleaseBuild_UsesUpdatedProfileShape()
    {
        var profile = new Profile("Release", OptimizeCode: true, GenerateDebugInfo: false);

        await Assert.That(profile.IsReleaseBuild).IsTrue();
    }

    [Test]
    public async Task ConfigBuilder_ProducesImmutableProperties()
    {
        var config = new ConfigBuilder()
                    .SetProperty("platform", new StringProperty("linux"))
                    .Build();

        await Assert.That(config.Properties).IsTypeOf<ImmutableDictionary<string, Property>>();
        await Assert.That(config.Properties["platform"].StringValue).IsEqualTo("linux");
        await Assert.That(config.Id).IsEqualTo(ConfigId.FromProperties(config.Properties));
    }

    [Test]
    public async Task ConfigId_FromProperties_IsStableForSameProperties()
    {
        var first = ImmutableDictionary.CreateRange(
            StringComparer.Ordinal,
            new Dictionary<string, Property>
            {
                ["platform"] = new StringProperty("linux"),
                ["features"] = new ListProperty(
                    [
                        new StringProperty("aot"),
                        new BooleanProperty(true)
                    ])
            });
        var second = ImmutableDictionary.CreateRange(
            StringComparer.Ordinal,
            new Dictionary<string, Property>
            {
                ["features"] = new ListProperty(
                    [
                        new StringProperty("aot"),
                        new BooleanProperty(true)
                    ]),
                ["platform"] = new StringProperty("linux")
            });

        await Assert.That(ConfigId.FromProperties(first)).IsEqualTo(ConfigId.FromProperties(second));
    }

    [Test]
    public async Task ConfigId_FromProperties_ChangesWhenPropertiesChange()
    {
        var first = ImmutableDictionary.CreateRange(
            StringComparer.Ordinal,
            new Dictionary<string, Property>
            {
                ["platform"] = new StringProperty("linux")
            });
        var second = ImmutableDictionary.CreateRange(
            StringComparer.Ordinal,
            new Dictionary<string, Property>
            {
                ["platform"] = new StringProperty("windows")
            });

        await Assert.That(ConfigId.FromProperties(first)).IsNotEqualTo(ConfigId.FromProperties(second));
    }

    [Test]
    public async Task ProjectBuilder_UsesLastPropertyValueWhenKeyRepeats()
    {
        var project = new ProjectBuilder()
                     .WithId(new ProjectId("/workspace/project"))
                     .SetProperty("configuration", new StringProperty("Debug"))
                     .SetProperty("configuration", new StringProperty("Release"))
                     .Build();

        await Assert.That(project.Properties["configuration"].StringValue).IsEqualTo("Release");
    }

    [Test]
    public async Task BuildingBuilder_BuildsGraphFromNestedBuilders()
    {
        var building = new BuildingBuilder()
                      .WithProfile(static profile => profile
                                                   .WithName("Debug")
                                                   .SetGenerateDebugInfo())
                      .WithConfig(static config => config
                                                 .SetProperty("runtime", new StringProperty("native")))
                      .AddProject(static project => project
                                                  .WithId(new ProjectId("/workspace/app"))
                                                  .SetProperty("lang", new StringProperty("csharp")))
                      .Build();

        await Assert.That(building.Profile.Name).IsEqualTo("Debug");
        await Assert.That(building.Profile.GenerateDebugInfo).IsTrue();
        await Assert.That(building.Config.Id).IsEqualTo(ConfigId.FromProperties(building.Config.Properties));
        await Assert.That(building.Projects.Length).IsEqualTo(1);
        await Assert.That(building.Projects[0].Properties["lang"].StringValue).IsEqualTo("csharp");
    }

    [Test]
    public async Task BuildingBuilder_Build_ThrowsWhenProfileMissing()
    {
        var builder = new BuildingBuilder()
                     .WithConfig(new ConfigBuilder().Build());

        Action action = () => _ = builder.Build();

        await Assert.That(action).ThrowsException()
            .WithMessage("Profile is required.");
    }

    [Test]
    public async Task BuildingBuilder_Build_ThrowsWhenConfigMissing()
    {
        var builder = new BuildingBuilder()
                     .WithProfile(new ProfileBuilder().WithName("Debug").Build());

        Action action = () => _ = builder.Build();

        await Assert.That(action).ThrowsException()
            .WithMessage("Config is required.");
    }

    [Test]
    public async Task EmptyConfig_GeneratesStableId()
    {
        var first = new ConfigBuilder().Build();
        var second = new ConfigBuilder().Build();

        await Assert.That(first.Id).IsEqualTo(second.Id);
    }

    [Test]
    public async Task ProjectBuilder_Build_ThrowsWhenIdMissing()
    {
        var builder = new ProjectBuilder();

        Action action = () => _ = builder.Build();

        await Assert.That(action).ThrowsException()
            .WithMessage("Project id is required.");
    }

    [Test]
    public async Task ProfileBuilder_Build_ThrowsWhenNameMissing()
    {
        var builder = new ProfileBuilder();

        Action action = () => _ = builder.Build();

        await Assert.That(action).ThrowsException()
            .WithMessage("Profile name is required.");
    }

    [Test]
    public async Task BuildingBuilder_Build_ThrowsWhenProjectIdsRepeat()
    {
        var sharedId = new ProjectId("/workspace/app");

        var builder = new BuildingBuilder()
                     .WithProfile(new ProfileBuilder().WithName("Debug").Build())
                     .WithConfig(new ConfigBuilder().Build())
                     .AddProject(new ProjectBuilder().WithId(sharedId).Build())
                     .AddProject(new ProjectBuilder().WithId(sharedId).Build());

        Action action = () => _ = builder.Build();

        await Assert.That(action).ThrowsException()
            .WithMessage($"Duplicate project id '{sharedId}' is not allowed.");
    }
}
