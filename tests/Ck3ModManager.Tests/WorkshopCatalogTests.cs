using Ck3ModManager.Core;

namespace Ck3ModManager.Tests;

public sealed class WorkshopCatalogTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("author")]
    public void SameItemIdAndTitleAcrossGamesRemainSeparate(string? creator)
    {
        var ck3 = Item(1, "Same Mod", creator);
        var hoi4 = ck3 with { ConsumerAppId = GameCatalog.Hoi4.AppId, UpdatedAt = ck3.UpdatedAt.AddDays(1) };

        var projects = WorkshopCatalog.Group([ck3, hoi4]);

        Assert.Equal(2, projects.Count);
        Assert.Equal(2, projects.Select(project => project.Key).Distinct().Count());
        Assert.Equal(new[] { Ck3.AppId, GameCatalog.Hoi4.AppId },
            projects.Select(project => Assert.Single(project.Releases).ConsumerAppId));
    }

    [Fact]
    public void Ck3TitleMarkerDoesNotDeclareHoi4Compatibility()
    {
        var item = Item(1, "Name [CK3 1.20]") with { ConsumerAppId = GameCatalog.Hoi4.AppId };

        var project = Assert.Single(WorkshopCatalog.Group([item]));
        var choice = Assert.Single(WorkshopCatalog.Rank(project, "1.20.1"));

        Assert.Equal(item.Title, project.Title);
        Assert.Empty(choice.GameVersions);
        Assert.Equal(CompatibilityStatus.Unknown, choice.Compatibility);
    }

    [Fact]
    public void EvaluateItemUsesWorkshopVersionTags()
    {
        var choice = WorkshopCatalog.EvaluateItem(Item(1, tags: ["1.20"]), "1.20.1");

        Assert.Equal(CompatibilityStatus.DeclaredMatch, choice.Compatibility);
        Assert.Equal(new[] { "1.20" }, choice.GameVersions);
    }

    [Fact]
    public void EvaluateItemStaysUnknownWithoutRecognizableTags()
    {
        var choice = WorkshopCatalog.EvaluateItem(Item(1, tags: ["Gameplay"]), "1.20.1");

        Assert.Equal(CompatibilityStatus.Unknown, choice.Compatibility);
    }

    [Fact]
    public void Hoi4TitleMarkerGroupsOnlyHoi4ReleasesAndProvidesVersionEvidence()
    {
        var first = Item(1, "Name [HOI4 1.15]") with { ConsumerAppId = GameCatalog.Hoi4.AppId };
        var second = Item(2, "Name (hoi4 1.16)") with { ConsumerAppId = GameCatalog.Hoi4.AppId };

        var project = Assert.Single(WorkshopCatalog.Group([first, second]));
        var choice = WorkshopCatalog.Rank(project, "1.16.1")[0];

        Assert.Equal("Name", project.Title);
        Assert.Equal(2UL, choice.Item.PublishedFileId);
        Assert.Equal(CompatibilityStatus.DeclaredMatch, choice.Compatibility);
        Assert.Equal(new[] { "1.16" }, choice.GameVersions);
        Assert.DoesNotContain("CK3", choice.Recommendation);
    }

    [Fact]
    public void GroupsSameCreatorAndCanonicalTitleWithoutChangingReleaseIdentities()
    {
        var first = Item(1, "  Café  Map ", "author");
        var second = Item(2, "cafe\u0301\tmap", "author");
        var project = Assert.Single(WorkshopCatalog.Group([first, second]));
        Assert.Equal("Café Map", project.Title);
        Assert.Equal(new[] { 1UL, 2UL }, project.Releases.Select(item => item.PublishedFileId));
        Assert.Same(first, project.Releases[0]);
        Assert.Same(second, project.Releases[1]);
    }

    [Fact]
    public void DoesNotMergeDifferentCreatorsWithTheSameTitle()
    {
        var groups = WorkshopCatalog.Group([Item(1, "Same Mod", "author-a"), Item(2, "Same Mod", "author-b")]);
        Assert.Equal(2, groups.Count);
        Assert.NotEqual(groups[0].Key, groups[1].Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("0")]
    public void MissingCreatorKeepsEachWorkshopItemIndependent(string? creator)
    {
        var groups = WorkshopCatalog.Group([Item(1, "Same Mod", creator), Item(2, "Same Mod", creator)]);
        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Single(group.Releases));
    }

    [Fact]
    public void EmptyTitlesDoNotCreateAnUnnamedAuthorGroup()
    {
        Assert.Equal(2, WorkshopCatalog.Group([Item(1, " ", "author"), Item(2, "\t", "author")]).Count);
    }

    [Fact]
    public void DuplicateWorkshopIdUsesNewestMetadataAndDoesNotCreateFakeReleases()
    {
        var oldCopy = Item(1, "Old name", null, updated: 1);
        var currentCopy = Item(1, "Current name", "author", updated: 2);
        var project = Assert.Single(WorkshopCatalog.Group([oldCopy, currentCopy, currentCopy]));
        Assert.Equal("Current name", project.Title);
        Assert.Same(currentCopy, Assert.Single(project.Releases));
    }

    [Theory]
    [InlineData("Mod 2", "Mod 3")]
    [InlineData("Mod v2.0", "Mod v3.0")]
    [InlineData("Mod", "Mod 汉化")]
    [InlineData("Mod", "Mod 补丁")]
    [InlineData("Mod [1.19]", "Mod [1.20]")]
    [InlineData("Mod CK3 1.19", "Mod CK3 1.20")]
    [InlineData("Mod [CK3 1.19] 汉化", "Mod [CK3 1.20] 汉化")]
    public void KeepsOrdinaryNumbersTranslationsPatchesAndNonSuffixMarkersSeparate(string first, string second)
    {
        Assert.Equal(2, WorkshopCatalog.Group([Item(1, first, "author"), Item(2, second, "author")]).Count);
    }

    [Fact]
    public void GroupsOnlyExplicitTrailingCk3VersionMarkersForTheSameCreator()
    {
        var projects = WorkshopCatalog.Group([
            Item(1, "Name [CK3 1.19]", "author"),
            Item(2, "Name (ck3 1.20)", "author"),
            Item(3, "Name - CK3 1.21", "author"),
            Item(4, "Name [CK3 1.20]", "other-author")
        ]);
        Assert.Equal(2, projects.Count);
        var project = Assert.Single(projects, group => group.Releases.Count == 3);
        Assert.Equal("Name", project.Title);
        Assert.Equal(new[] { 1UL, 2UL, 3UL }, project.Releases.Select(item => item.PublishedFileId));
        var recommended = WorkshopCatalog.Rank(project, "1.20.2");
        Assert.Equal(2UL, recommended[0].Item.PublishedFileId);
        Assert.Equal(CompatibilityStatus.DeclaredMatch, recommended[0].Compatibility);
        Assert.Equal(new[] { "1.20" }, recommended[0].GameVersions);
        Assert.Contains("标题", recommended[0].Recommendation);
    }

    [Fact]
    public void ExplicitGameSuffixDoesNotEraseTheModVersionOrTranslationIdentity()
    {
        var projects = WorkshopCatalog.Group([
            Item(1, "Name v2.0 汉化 [CK3 1.19]", "author"),
            Item(2, "Name v2.0 汉化 (CK3 1.20)", "author"),
            Item(3, "Name v3.0 汉化 [CK3 1.20]", "author"),
            Item(4, "Name v2.0 补丁 [CK3 1.20]", "author")
        ]);
        Assert.Equal(3, projects.Count);
        var project = Assert.Single(projects, group => group.Releases.Count == 2);
        Assert.Equal("Name v2.0 汉化", project.Title);
        Assert.All(WorkshopCatalog.Rank(project, "1.20.1"), choice => Assert.Equal("v2.0", choice.ModVersion));
    }

    [Fact]
    public void BareVersionSuffixesGroupOnlyWhenEachItemHasItsOwnExactlyMatchingGameTag()
    {
        var project = Assert.Single(WorkshopCatalog.Group([
            Item(1, "Name [1.19]", tags: ["1.19 'Basileus'"]),
            Item(2, "Name (1.20)", tags: ["1.20 'Crozier'"]),
            Item(3, "Name - 1.21", tags: ["1.21"]),
            Item(4, "Name [CK3 1.22]")
        ]));
        Assert.Equal("Name", project.Title);
        Assert.Equal(4, project.Releases.Count);
        var choices = WorkshopCatalog.Rank(project, "1.20.3");
        Assert.Equal(2UL, choices[0].Item.PublishedFileId);
        Assert.Equal(CompatibilityStatus.DeclaredMatch, choices[0].Compatibility);
    }

    [Theory]
    [InlineData("Name [1.20]", "1.19")]
    [InlineData("Name (1.20.3)", "1.20")]
    [InlineData("Name - 1.20", "v1.20")]
    [InlineData("Name [1.20]", "Graphics")]
    [InlineData("Name (1.20) 汉化", "1.20")]
    [InlineData("Name - v2.0", "2.0")]
    public void BareNumericSuffixWithMissingOrDifferentEvidenceRemainsPartOfIdentity(string title, string tag)
    {
        var projects = WorkshopCatalog.Group([Item(1, title, tags: [tag]), Item(2, "Name", tags: [tag])]);
        Assert.Equal(2, projects.Count);
        Assert.Equal(title, projects[0].Title);
    }

    [Fact]
    public void OneReleasesTagsCannotJustifyStrippingAnotherReleasesNumericSuffix()
    {
        var projects = WorkshopCatalog.Group([
            Item(1, "Name [1.20]", tags: ["Graphics"]),
            Item(2, "Name [1.19]", tags: ["1.19", "1.20"])
        ]);
        Assert.Equal(2, projects.Count);
        Assert.Equal("Name [1.20]", projects[0].Title);
        Assert.Equal("Name", projects[1].Title);
    }

    [Fact]
    public void SameItemWithSeveralVersionTagsIsOneReleaseChoice()
    {
        var project = Project(Item(1, tags: ["Events", "1.19 'Basileus'", "1.20 'Crozier'", "1.20"]));
        var choice = Assert.Single(WorkshopCatalog.Rank(project, "1.20.3"));
        Assert.Equal(new[] { "1.19", "1.20" }, choice.GameVersions);
        Assert.Equal(CompatibilityStatus.DeclaredMatch, choice.Compatibility);
        Assert.Null(choice.ModVersion);
        Assert.Contains("标签声明", choice.Recommendation);
        Assert.Contains("非实测", choice.Recommendation);
    }

    [Theory]
    [InlineData("1.20", "1.20")]
    [InlineData("1.20 'Crozier'", "1.20")]
    [InlineData("1.20 \"Crozier\"", "1.20")]
    [InlineData("1.20 ‘Crozier’", "1.20")]
    [InlineData("1.20.1.2", "1.20.1.2")]
    public void ReadsUnambiguousNumericGameVersionTags(string tag, string expected)
    {
        var choice = Assert.Single(WorkshopCatalog.Rank(Project(Item(1, tags: [tag])), "1.20.1.2"));
        Assert.Equal(new[] { expected }, choice.GameVersions);
    }

    [Theory]
    [InlineData("Graphics")]
    [InlineData("v2.0")]
    [InlineData("适配1.20")]
    [InlineData("1.19 or 1.20")]
    [InlineData(">=1.20")]
    public void DoesNotInferCompatibilityFromAmbiguousTags(string tag)
    {
        var choice = Assert.Single(WorkshopCatalog.Rank(Project(Item(1, tags: [tag])), "1.20.1"));
        Assert.Empty(choice.GameVersions);
        Assert.Equal(CompatibilityStatus.Unknown, choice.Compatibility);
        Assert.Contains("未知", choice.Recommendation);
        Assert.Contains("非实测", choice.Recommendation);
    }

    [Fact]
    public void KnownVersionTagsTakePrecedenceOverTitleFallback()
    {
        var choice = Assert.Single(WorkshopCatalog.Rank(Project(Item(1, "Name [CK3 1.20]", tags: ["1.19"])), "1.20.1"));
        Assert.Equal(new[] { "1.19" }, choice.GameVersions);
        Assert.Equal(CompatibilityStatus.DeclaredMismatch, choice.Compatibility);
        Assert.Contains("标签声明", choice.Recommendation);
    }

    [Fact]
    public void RanksMatchBeforeUnknownAndMismatchRegardlessOfWhichItemIsNewest()
    {
        var matching = Item(1, updated: 1, tags: ["1.20"]);
        var unknown = Item(2, updated: 100, tags: ["Events"]);
        var mismatching = Item(3, updated: 200, tags: ["1.19"]);
        var choices = WorkshopCatalog.Rank(Project(mismatching, unknown, matching), "1.20.2");
        Assert.Equal(new[] { 1UL, 2UL, 3UL }, choices.Select(choice => choice.Item.PublishedFileId));
        Assert.Equal(new[] { CompatibilityStatus.DeclaredMatch, CompatibilityStatus.Unknown, CompatibilityStatus.DeclaredMismatch }, choices.Select(choice => choice.Compatibility));
        Assert.All(choices, choice => Assert.Contains("非实测", choice.Recommendation));
    }

    [Fact]
    public void OrdersByUpdateTimeInsideTheSameDeclaredCompatibility()
    {
        var choices = WorkshopCatalog.Rank(Project(Item(1, updated: 1, tags: ["1.20"]), Item(2, updated: 2, tags: ["1.20"])), "1.20.2");
        Assert.Equal(new[] { 2UL, 1UL }, choices.Select(choice => choice.Item.PublishedFileId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("未识别")]
    public void MissingOrUnparseableLocalVersionLeavesAllChoicesUnknown(string? actual)
    {
        var choices = WorkshopCatalog.Rank(Project(Item(1, updated: 1, tags: ["1.20"]), Item(2, updated: 2, tags: ["1.19"])), actual);
        Assert.All(choices, choice =>
        {
            Assert.Equal(CompatibilityStatus.Unknown, choice.Compatibility);
            Assert.Contains("未检测", choice.Recommendation);
            Assert.Contains("非实测", choice.Recommendation);
            Assert.DoesNotContain("优先", choice.Recommendation);
        });
        Assert.Equal(new[] { 2UL, 1UL }, choices.Select(choice => choice.Item.PublishedFileId));
    }

    [Fact]
    public void IncompleteLocalVersionDoesNotTurnMoreSpecificDeclarationIntoMismatch()
    {
        var choice = Assert.Single(WorkshopCatalog.Rank(Project(Item(1, tags: ["1.20.3"])), "1.20"));
        Assert.Equal(CompatibilityStatus.Unknown, choice.Compatibility);
    }

    [Theory]
    [InlineData("Name v2.0", "v2.0")]
    [InlineData("Name V02.03", "V02.03")]
    [InlineData("Name v2.0-beta", "v2.0-beta")]
    [InlineData("Name v2.0 [CK3 1.20]", "v2.0")]
    [InlineData("Name 2.0", null)]
    [InlineData("Name [CK3 1.20]", null)]
    [InlineData("Name v2.0 v3.0", null)]
    [InlineData("Name v2", null)]
    [InlineData("Name [CK3 v1.20]", null)]
    [InlineData("Name v2.0 for CK3 v1.20", "v2.0")]
    public void RetainsOnlyAnUnambiguousExplicitModVersionFromTitle(string title, string? expected)
    {
        var choice = Assert.Single(WorkshopCatalog.Rank(Project(Item(1, title)), "1.20.1"));
        Assert.Equal(expected, choice.ModVersion);
    }

    [Fact]
    public void GroupAndRankDoNotMutateInputOrderOrDiscardActualWorkshopItems()
    {
        var items = new[] { Item(2, "Name", "author", 2), Item(1, "Name", "author", 1) };
        var project = Assert.Single(WorkshopCatalog.Group(items));
        var before = project.Releases.ToArray();
        var first = WorkshopCatalog.Rank(project, null);
        var second = WorkshopCatalog.Rank(project, null);
        Assert.Equal(new[] { 2UL, 1UL }, items.Select(item => item.PublishedFileId));
        Assert.Equal(before, project.Releases);
        Assert.Equal(first.Select(choice => (choice.Item.PublishedFileId, choice.Compatibility, choice.Recommendation)),
            second.Select(choice => (choice.Item.PublishedFileId, choice.Compatibility, choice.Recommendation)));
    }

    [Fact]
    public void GroupKeysAreStableAndEscapeSeparators()
    {
        var items = new[] { Item(1, "B/title/C", "A"), Item(2, "C", "A/title/B") };
        var first = WorkshopCatalog.Group(items);
        var second = WorkshopCatalog.Group(items);
        Assert.Equal(first.Select(group => group.Key), second.Select(group => group.Key));
        Assert.Equal(2, first.Select(group => group.Key).Distinct().Count());
    }

    private static WorkshopProject Project(params WorkshopItem[] releases) => new("fixture", "Fixture", releases);

    private static WorkshopItem Item(ulong id, string title = "Name", string? creator = "author", int updated = 1, IReadOnlyList<string>? tags = null) =>
        new(id, Ck3.AppId, title, "说明", null, new Uri($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}"),
            DateTimeOffset.UnixEpoch.AddDays(updated), 100, tags ?? [], null, CreatorId: creator);
}
