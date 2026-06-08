using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Validation;

namespace Verbara.Platform.Typification.Tests;

public sealed class DefaultTypificationValidatorTests
{
    private readonly DefaultTypificationValidator _validator = new();

    // ---------- ValidateForPublish ----------

    [Fact]
    public void ValidateForPublish_ShouldFail_WhenDepthExceedsMaxDepth()
    {
        // root -> child -> leaf = depth 3, MaxDepth 2.
        var root = Node("root", "ROOT", isLeaf: false);
        var mid = Node("mid", "MID", isLeaf: false, parent: root.NodeId);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: mid.NodeId, leaf: Outcome());

        var schema = Schema(maxDepth: 2, nodes: [root, mid, leaf]);

        var result = _validator.ValidateForPublish(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("exceeds MaxDepth"));
    }

    [Fact]
    public void ValidateForPublish_ShouldFail_WhenNodeCodeNotUniqueWithinSchema()
    {
        var root = Node("root", "DUP", isLeaf: false);
        var leaf = Node("leaf", "DUP", isLeaf: true, parent: root.NodeId, leaf: Outcome());

        var schema = Schema(nodes: [root, leaf]);

        var result = _validator.ValidateForPublish(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("not unique"));
    }

    [Fact]
    public void ValidateForPublish_ShouldFail_WhenLeafHasNoOutcome()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: null);

        var schema = Schema(nodes: [root, leaf]);

        var result = _validator.ValidateForPublish(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("non-null Leaf outcome"));
    }

    [Fact]
    public void ValidateForPublish_ShouldFail_WhenVisibleWhenRefsUnknownFieldKey()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());

        var field = Field(
            "notes",
            FieldType.Text,
            visibleWhen: new ConditionExpr
            {
                RefType = ConditionRef.Field,
                Ref = "does-not-exist",
                Op = ConditionOp.Eq,
                Value = "x",
            });

        var schema = Schema(nodes: [root, leaf], fields: [field]);

        var result = _validator.ValidateForPublish(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("unknown field Key"));
    }

    [Fact]
    public void ValidateForPublish_ShouldSucceed_WhenValidThreeLevelCascade()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var mid = Node("mid", "MID", isLeaf: false, parent: root.NodeId);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: mid.NodeId, leaf: Outcome());

        var field = Field("reason", FieldType.Text, required: true, attachTo: leaf.NodeId);

        var schema = Schema(maxDepth: 5, nodes: [root, mid, leaf], fields: [field]);

        var result = _validator.ValidateForPublish(schema);

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    // ---------- ValidateSubmission ----------

    [Fact]
    public void ValidateSubmission_ShouldReject_WhenRequiredVisibleFieldMissing()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());
        var field = Field("reason", FieldType.Text, required: true);

        var schema = Schema(nodes: [root, leaf], fields: [field]);

        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, leaf.NodeId],
            new Dictionary<string, string>());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "reason" && e.Message.Contains("required"));
    }

    [Fact]
    public void ValidateSubmission_ShouldAccept_WhenRequiredFieldHiddenByCondition()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());

        // 'details' is required but only visible when 'category' == 'other'.
        var category = Field("category", FieldType.Text);
        var details = Field(
            "details",
            FieldType.Text,
            required: true,
            visibleWhen: new ConditionExpr
            {
                RefType = ConditionRef.Field,
                Ref = "category",
                Op = ConditionOp.Eq,
                Value = "other",
            });

        var schema = Schema(nodes: [root, leaf], fields: [category, details]);

        // category == 'general' => details hidden => not required.
        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, leaf.NodeId],
            new Dictionary<string, string> { ["category"] = "general" });

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void ValidateSubmission_ShouldReject_WhenSelectedPathDoesNotEndAtLeaf()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var mid = Node("mid", "MID", isLeaf: false, parent: root.NodeId);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: mid.NodeId, leaf: Outcome());

        var schema = Schema(nodes: [root, mid, leaf]);

        // Path stops at 'mid' which is not a leaf.
        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, mid.NodeId],
            new Dictionary<string, string>());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "path" && e.Message.Contains("leaf"));
    }

    [Fact]
    public void ValidateSubmission_ShouldReject_WhenNumberFieldNotParseable()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());
        var amount = Field("amount", FieldType.Number);

        var schema = Schema(nodes: [root, leaf], fields: [amount]);

        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, leaf.NodeId],
            new Dictionary<string, string> { ["amount"] = "not-a-number" });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "amount" && e.Message.Contains("number"));
    }

    // ---------- EvaluateCondition ----------

    [Fact]
    public void EvaluateCondition_ShouldReturnTrue_WhenOpInAndValueInCsv()
    {
        var expr = new ConditionExpr
        {
            RefType = ConditionRef.Field,
            Ref = "color",
            Op = ConditionOp.In,
            Value = "red, green, blue",
        };

        var result = _validator.EvaluateCondition(
            expr,
            new Dictionary<string, string> { ["color"] = "green" },
            new HashSet<string>());

        result.Should().BeTrue();
    }

    [Fact]
    public void EvaluateCondition_ShouldReturnFalse_WhenReferencedFieldAbsentAndOpEq()
    {
        var expr = new ConditionExpr
        {
            RefType = ConditionRef.Field,
            Ref = "missing",
            Op = ConditionOp.Eq,
            Value = "yes",
        };

        var result = _validator.EvaluateCondition(
            expr,
            new Dictionary<string, string>(),
            new HashSet<string>());

        result.Should().BeFalse();
    }

    // ---------- Builders ----------

    private static TypificationNode Node(
        string id,
        string code,
        bool isLeaf,
        EntityId? parent = null,
        LeafOutcome? leaf = null) =>
        new()
        {
            NodeId = EntityId.From(id),
            ParentNodeId = parent,
            Label = code,
            Code = code,
            IsLeaf = isLeaf,
            Leaf = leaf,
        };

    private static LeafOutcome Outcome() =>
        new() { Category = TypificationCategory.Success };

    private static TypificationField Field(
        string key,
        FieldType type,
        bool required = false,
        EntityId? attachTo = null,
        ConditionExpr? visibleWhen = null,
        IReadOnlyList<FieldOption>? options = null,
        FieldValidation? validation = null) =>
        new()
        {
            FieldId = EntityId.From($"field-{key}"),
            Key = key,
            Label = key,
            Type = type,
            Required = required,
            AttachToNodeId = attachTo,
            VisibleWhen = visibleWhen,
            Options = options,
            Validation = validation,
        };

    private static TypificationSchema Schema(
        int maxDepth = 5,
        IReadOnlyList<TypificationNode>? nodes = null,
        IReadOnlyList<TypificationField>? fields = null) =>
        new()
        {
            SchemaId = EntityId.From("schema-1"),
            TenantId = new TenantId("tenant-1"),
            Name = "Test schema",
            Version = 1,
            MaxDepth = maxDepth,
            Nodes = nodes ?? [],
            Fields = fields ?? [],
            DataDips = [],
            AiConfig = new TypificationAiConfig { EntityFieldMap = new Dictionary<string, string>() },
        };
}
