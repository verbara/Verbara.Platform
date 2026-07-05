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

    // ---------- ValidateSubmission — D3 Source-aware free-text length cap ----------

    [Fact]
    public void ValidateSubmission_ShouldRejectOverlongText_WhenSourceIsAutoAi()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());
        // Free-text field with NO MaxLength configured → AI cap (2000) applies.
        var notes = Field("notes", FieldType.Text);

        var schema = Schema(nodes: [root, leaf], fields: [notes]);

        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, leaf.NodeId],
            new Dictionary<string, string> { ["notes"] = new string('x', 2001) },
            SubmissionSource.AutoAi);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "notes" && e.Message.Contains("2000"));
    }

    [Fact]
    public void ValidateSubmission_ShouldAllowOverlongText_WhenSourceIsManual()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());
        var notes = Field("notes", FieldType.Text);

        var schema = Schema(nodes: [root, leaf], fields: [notes]);

        // Default source (Manual) → the AI cap does NOT apply; current behavior preserved.
        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, leaf.NodeId],
            new Dictionary<string, string> { ["notes"] = new string('x', 2001) });

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void ValidateSubmission_ShouldRespectFieldMaxLength_WhenSmaller()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());
        // Configured MaxLength (50) is smaller than the AI cap (2000): the smaller wins,
        // and only a SINGLE error is reported (no double-report).
        var notes = Field("notes", FieldType.Text, validation: new FieldValidation { MaxLength = 50 });

        var schema = Schema(nodes: [root, leaf], fields: [notes]);

        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, leaf.NodeId],
            new Dictionary<string, string> { ["notes"] = new string('x', 100) },
            SubmissionSource.AutoAi);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Field == "notes");
        result.Errors.Should().Contain(e => e.Field == "notes" && e.Message.Contains("50"));
    }

    [Fact]
    public void ValidateSubmission_ShouldClampToAiCap_WhenFieldMaxLengthExceedsAiCap()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());
        // Configured MaxLength (5000) is LARGER than the AI cap (2000): the AI cap clamps below
        // the configured length, so a 3000-char AI value (under the configured 5000 but over the
        // 2000 cap) is rejected — proving the cap binds when the configured length is larger.
        var notes = Field("notes", FieldType.Text, validation: new FieldValidation { MaxLength = 5000 });

        var schema = Schema(nodes: [root, leaf], fields: [notes]);

        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, leaf.NodeId],
            new Dictionary<string, string> { ["notes"] = new string('x', 3000) },
            SubmissionSource.AutoAi);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "notes" && e.Message.Contains("2000"));
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

    // ---------- ValidateForPublish — schema-integrity guards ----------

    [Fact]
    public void ValidateForPublish_ShouldFail_WhenMaxDepthBelowMinimum()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());

        var schema = Schema(maxDepth: 0, nodes: [root, leaf]);

        var result = _validator.ValidateForPublish(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "maxDepth" && e.Message.Contains("MaxDepth must be between"));
    }

    [Fact]
    public void ValidateForPublish_ShouldFail_WhenMaxDepthAboveMaximum()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());

        var schema = Schema(maxDepth: 9, nodes: [root, leaf]);

        var result = _validator.ValidateForPublish(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "maxDepth" && e.Message.Contains("MaxDepth must be between"));
    }

    [Fact]
    public void ValidateForPublish_ShouldFail_WhenDuplicateNodeId()
    {
        // Same NodeId "dup" used twice — TryAdd fails on the second insertion.
        var first = Node("dup", "ONE", isLeaf: true, leaf: Outcome());
        var second = Node("dup", "TWO", isLeaf: true, leaf: Outcome());

        var schema = Schema(nodes: [first, second]);

        var result = _validator.ValidateForPublish(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("Duplicate node id"));
    }

    [Fact]
    public void ValidateForPublish_ShouldFail_WhenFieldKeyNotUnique()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());

        var a = Field("dup", FieldType.Text);
        var b = Field("dup", FieldType.Number);

        var schema = Schema(nodes: [root, leaf], fields: [a, b]);

        var result = _validator.ValidateForPublish(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "fields" && e.Message.Contains("Field Key 'dup' is not unique"));
    }

    [Fact]
    public void ValidateForPublish_ShouldFail_WhenNodeReferencesMissingParent()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        // Parent id 'ghost' is not present in the schema.
        var orphan = Node("orphan", "ORPHAN", isLeaf: true, parent: EntityId.From("ghost"), leaf: Outcome());

        var schema = Schema(nodes: [root, orphan]);

        var result = _validator.ValidateForPublish(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("references a missing parent node id"));
    }

    [Fact]
    public void ValidateForPublish_ShouldFail_WhenParentCycleExists()
    {
        // a -> b -> a : a mutual parent cycle.
        var a = Node("a", "A", isLeaf: false, parent: EntityId.From("b"));
        var b = Node("b", "B", isLeaf: false, parent: EntityId.From("a"));

        var schema = Schema(nodes: [a, b]);

        var result = _validator.ValidateForPublish(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("part of a parent cycle"));
    }

    [Fact]
    public void ValidateForPublish_ShouldFail_WhenNodeHasChildrenButMarkedLeaf()
    {
        // root has a child yet is flagged IsLeaf=true.
        var root = Node("root", "ROOT", isLeaf: true, leaf: Outcome());
        var child = Node("child", "CHILD", isLeaf: true, parent: root.NodeId, leaf: Outcome());

        var schema = Schema(nodes: [root, child]);

        var result = _validator.ValidateForPublish(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("must not be a leaf"));
    }

    [Fact]
    public void ValidateForPublish_ShouldFail_WhenNonLeafHasOutcome()
    {
        // root is a non-leaf (has a child) but carries a Leaf outcome.
        var root = Node("root", "ROOT", isLeaf: false, leaf: Outcome());
        var child = Node("child", "CHILD", isLeaf: true, parent: root.NodeId, leaf: Outcome());

        var schema = Schema(nodes: [root, child]);

        var result = _validator.ValidateForPublish(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("must have a null Leaf outcome"));
    }

    [Fact]
    public void ValidateForPublish_ShouldFail_WhenNonLeafHasNoChildren()
    {
        // Single non-leaf root with no children.
        var root = Node("root", "ROOT", isLeaf: false, leaf: null);

        var schema = Schema(nodes: [root]);

        var result = _validator.ValidateForPublish(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("must have at least one child"));
    }

    [Fact]
    public void ValidateForPublish_ShouldFail_WhenFieldAttachToNodeMissing()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());

        var field = Field("reason", FieldType.Text, attachTo: EntityId.From("ghost"));

        var schema = Schema(nodes: [root, leaf], fields: [field]);

        var result = _validator.ValidateForPublish(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("AttachToNodeId references a missing node id"));
    }

    [Fact]
    public void ValidateForPublish_ShouldFail_WhenVisibleWhenRefsUnknownNodeCode()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());

        var field = Field(
            "notes",
            FieldType.Text,
            visibleWhen: new ConditionExpr
            {
                RefType = ConditionRef.NodeSelected,
                Ref = "NO-SUCH-CODE",
                Op = ConditionOp.Eq,
                Value = "true",
            });

        var schema = Schema(nodes: [root, leaf], fields: [field]);

        var result = _validator.ValidateForPublish(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("unknown node Code"));
    }

    [Fact]
    public void ValidateForPublish_ShouldFail_WhenVisibleWhenHasUnsupportedRefType()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());

        var field = Field(
            "notes",
            FieldType.Text,
            visibleWhen: new ConditionExpr
            {
                RefType = (ConditionRef)999,
                Ref = "whatever",
                Op = ConditionOp.Eq,
                Value = "x",
            });

        var schema = Schema(nodes: [root, leaf], fields: [field]);

        var result = _validator.ValidateForPublish(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("unsupported RefType"));
    }

    // ---------- ValidateSubmission — typed value: Number range ----------

    [Theory]
    [InlineData("5", false)]    // below Min (10)
    [InlineData("10", true)]    // Min boundary (inclusive)
    [InlineData("100", true)]   // Max boundary (inclusive)
    [InlineData("150", false)]  // above Max (100)
    public void ValidateSubmission_ShouldEnforceRange_WhenNumberFieldHasMinMax(string value, bool expectedValid)
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());
        var amount = Field("amount", FieldType.Number, validation: new FieldValidation { Min = 10, Max = 100 });

        var schema = Schema(nodes: [root, leaf], fields: [amount]);

        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, leaf.NodeId],
            new Dictionary<string, string> { ["amount"] = value });

        result.IsValid.Should().Be(expectedValid, because: string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    // ---------- ValidateSubmission — typed value: Date ----------

    [Theory]
    [InlineData("2026-01-15", true)]
    [InlineData("not-a-date", false)]
    public void ValidateSubmission_ShouldValidateDate_WhenDateField(string value, bool expectedValid)
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());
        var when = Field("when", FieldType.Date);

        var schema = Schema(nodes: [root, leaf], fields: [when]);

        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, leaf.NodeId],
            new Dictionary<string, string> { ["when"] = value });

        result.IsValid.Should().Be(expectedValid, because: string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    // ---------- ValidateSubmission — typed value: Phone ----------

    [Theory]
    [InlineData("+1 (555) 123-4567", true)]  // valid separators, enough digits
    [InlineData("+1-23", false)]             // fewer than 5 digits
    [InlineData("555-CALL", false)]          // contains a disallowed (letter) character
    public void ValidateSubmission_ShouldValidatePhone_WhenPhoneField(string value, bool expectedValid)
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());
        var phone = Field("phone", FieldType.Phone);

        var schema = Schema(nodes: [root, leaf], fields: [phone]);

        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, leaf.NodeId],
            new Dictionary<string, string> { ["phone"] = value });

        result.IsValid.Should().Be(expectedValid, because: string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    // ---------- ValidateSubmission — typed value: Boolean ----------

    [Theory]
    [InlineData("true", true)]
    [InlineData("FALSE", true)]   // case-insensitive
    [InlineData("maybe", false)]
    public void ValidateSubmission_ShouldValidateBoolean_WhenBooleanField(string value, bool expectedValid)
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());
        var flag = Field("flag", FieldType.Boolean);

        var schema = Schema(nodes: [root, leaf], fields: [flag]);

        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, leaf.NodeId],
            new Dictionary<string, string> { ["flag"] = value });

        result.IsValid.Should().Be(expectedValid, because: string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    // ---------- ValidateSubmission — typed value: Select ----------

    [Theory]
    [InlineData("a", true)]
    [InlineData("z", false)]  // not an allowed option
    public void ValidateSubmission_ShouldValidateSelect_WhenSelectField(string value, bool expectedValid)
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());
        var choice = Field(
            "choice",
            FieldType.Select,
            options: [new FieldOption { Value = "a", Label = "A" }, new FieldOption { Value = "b", Label = "B" }]);

        var schema = Schema(nodes: [root, leaf], fields: [choice]);

        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, leaf.NodeId],
            new Dictionary<string, string> { ["choice"] = value });

        result.IsValid.Should().Be(expectedValid, because: string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void ValidateSubmission_ShouldReject_WhenSelectFieldHasNoOptions()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());
        // Select field with a null Options list — every value is disallowed.
        var choice = Field("choice", FieldType.Select);

        var schema = Schema(nodes: [root, leaf], fields: [choice]);

        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, leaf.NodeId],
            new Dictionary<string, string> { ["choice"] = "anything" });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "choice" && e.Message.Contains("not an allowed option"));
    }

    // ---------- ValidateSubmission — typed value: MultiSelect ----------

    [Theory]
    [InlineData("a,,b,", true)]  // empty parts (trailing/double separators) are skipped
    [InlineData("a,b", true)]
    [InlineData("a,z", false)]   // 'z' is not an allowed option
    public void ValidateSubmission_ShouldValidateMultiSelect_WhenMultiSelectField(string value, bool expectedValid)
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());
        var tags = Field(
            "tags",
            FieldType.MultiSelect,
            options: [new FieldOption { Value = "a", Label = "A" }, new FieldOption { Value = "b", Label = "B" }]);

        var schema = Schema(nodes: [root, leaf], fields: [tags]);

        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, leaf.NodeId],
            new Dictionary<string, string> { ["tags"] = value });

        result.IsValid.Should().Be(expectedValid, because: string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    // ---------- ValidateSubmission — Regex / MaxLength (ApplyStringValidation) ----------

    [Fact]
    public void ValidateSubmission_ShouldReject_WhenRegexPatternIsInvalid()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());
        // "[" is an unterminated character class → Regex throws ArgumentException.
        var code = Field("code", FieldType.Text, validation: new FieldValidation { Regex = "[" });

        var schema = Schema(nodes: [root, leaf], fields: [code]);

        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, leaf.NodeId],
            new Dictionary<string, string> { ["code"] = "abc" });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "code" && e.Message.Contains("invalid validation pattern"));
    }

    [Fact]
    public void ValidateSubmission_ShouldReject_WhenRegexTimesOut()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());
        // Catastrophic-backtracking pattern against a long non-matching input: ~2^50 attempts,
        // far exceeding the 100ms RegexTimeout, so a RegexMatchTimeoutException is raised.
        var code = Field("code", FieldType.Text, validation: new FieldValidation { Regex = "^(a+)+$" });

        var schema = Schema(nodes: [root, leaf], fields: [code]);

        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, leaf.NodeId],
            new Dictionary<string, string> { ["code"] = new string('a', 50) + "!" });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "code" && e.Message.Contains("timed out"));
    }

    [Fact]
    public void ValidateSubmission_ShouldReject_WhenValueDoesNotMatchRegex()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());
        var code = Field("code", FieldType.Text, validation: new FieldValidation { Regex = "^[0-9]+$" });

        var schema = Schema(nodes: [root, leaf], fields: [code]);

        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, leaf.NodeId],
            new Dictionary<string, string> { ["code"] = "abc" });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "code" && e.Message.Contains("does not match the required format"));
    }

    [Fact]
    public void ValidateSubmission_ShouldAccept_WhenValueMatchesRegex()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());
        var code = Field("code", FieldType.Text, validation: new FieldValidation { Regex = "^[0-9]+$" });

        var schema = Schema(nodes: [root, leaf], fields: [code]);

        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, leaf.NodeId],
            new Dictionary<string, string> { ["code"] = "12345" });

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void ValidateSubmission_ShouldReject_WhenManualValueExceedsConfiguredMaxLength()
    {
        var root = Node("root", "ROOT", isLeaf: false);
        var leaf = Node("leaf", "LEAF", isLeaf: true, parent: root.NodeId, leaf: Outcome());
        // Manual source (default) → the configured per-field MaxLength applies (no AI cap wording).
        var notes = Field("notes", FieldType.Text, validation: new FieldValidation { MaxLength = 5 });

        var schema = Schema(nodes: [root, leaf], fields: [notes]);

        var result = _validator.ValidateSubmission(
            schema,
            [root.NodeId, leaf.NodeId],
            new Dictionary<string, string> { ["notes"] = "0123456789" });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "notes" && e.Message.Contains("exceeds the maximum length of 5"));
        result.Errors.Should().NotContain(e => e.Message.Contains("AI-sourced"));
    }

    // ---------- EvaluateCondition — operators & ref types ----------

    [Theory]
    [InlineData("green", ConditionOp.Neq, "red", true)]              // differ → true
    [InlineData("red", ConditionOp.Neq, "red", false)]              // equal → false
    [InlineData("yellow", ConditionOp.In, "red,green,blue", false)] // not in CSV
    [InlineData("HELLO world", ConditionOp.Contains, "hello", true)] // case-insensitive substring
    [InlineData("something", ConditionOp.Exists, null, true)]        // non-empty → exists
    [InlineData("", ConditionOp.Exists, null, false)]               // empty → not exists
    [InlineData("10", ConditionOp.GreaterThan, "5", true)]          // numeric greater
    [InlineData("abc", ConditionOp.GreaterThan, "5", false)]        // non-numeric left → false
    [InlineData("3", ConditionOp.LessThan, "5", true)]             // numeric less
    public void EvaluateCondition_ShouldReturnExpected_WhenFieldRefAcrossOperators(
        string left,
        ConditionOp op,
        string? value,
        bool expected)
    {
        var expr = new ConditionExpr
        {
            RefType = ConditionRef.Field,
            Ref = "f",
            Op = op,
            Value = value,
        };

        var result = _validator.EvaluateCondition(
            expr,
            new Dictionary<string, string> { ["f"] = left },
            new HashSet<string>());

        result.Should().Be(expected);
    }

    [Fact]
    public void EvaluateCondition_ShouldReturnTrue_WhenRefTypeNodeSelectedAndCodePresent()
    {
        var expr = new ConditionExpr
        {
            RefType = ConditionRef.NodeSelected,
            Ref = "VIP",
            Op = ConditionOp.Eq,
            Value = "true",
        };

        var result = _validator.EvaluateCondition(
            expr,
            new Dictionary<string, string>(),
            new HashSet<string> { "VIP" });

        result.Should().BeTrue();
    }

    [Fact]
    public void EvaluateCondition_ShouldReturnFalse_WhenOperatorUnsupported()
    {
        var expr = new ConditionExpr
        {
            RefType = ConditionRef.Field,
            Ref = "f",
            Op = (ConditionOp)999,
            Value = "x",
        };

        var result = _validator.EvaluateCondition(
            expr,
            new Dictionary<string, string> { ["f"] = "x" },
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
