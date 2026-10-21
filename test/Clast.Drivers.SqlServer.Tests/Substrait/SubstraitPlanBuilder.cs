// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
//
// Derived from kusto_adbc (test/KustoAdbc.Tests/SubstraitPlanBuilder.cs);
// see THIRD-PARTY-NOTICES.md. Changes: namespace; more types, literals,
// relations and expressions (virtual tables, mark joins, all set operations,
// WITH TIES, ordered aggregates, subqueries and outer references).

using System.Text;

namespace Clast.Drivers.SqlServer.Tests.Substrait;

/// <summary>
/// Builds Substrait plans in protobuf wire format for tests.
///
/// Field numbers are written out here from the Substrait .proto files
/// (substrait-io/substrait v0.107: proto/substrait/{plan,algebra,type}.proto,
/// extensions/extensions.proto) rather than taken from the driver's
/// SubstraitFields, so that a wrong constant in the driver cannot be hidden
/// by the tests using the same wrong value.
///
/// Relation builders return complete Rel messages; expression builders return
/// complete Expression messages.
/// </summary>
static class P
{
    #region Wire format

    public static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        while (value >= 0x80) { bytes.Add((byte)(value | 0x80)); value >>= 7; }
        bytes.Add((byte)value);
        return bytes.ToArray();
    }

    public static byte[] Tag(int field, int wireType) => Varint((ulong)((field << 3) | wireType));

    public static byte[] VarField(int field, long value) => Cat(Tag(field, 0), Varint((ulong)value));

    public static byte[] LenDel(int field, byte[] content) => Cat(Tag(field, 2), Varint((ulong)content.Length), content);

    public static byte[] StrField(int field, string value) => LenDel(field, Encoding.UTF8.GetBytes(value));

    public static byte[] Fixed64Field(int field, long value) => Cat(Tag(field, 1), BitConverter.GetBytes(value));

    public static byte[] Packed(int field, IEnumerable<int> values)
        => LenDel(field, Cat(values.Select(v => Varint((ulong)(long)v)).ToArray()));

    public static byte[] Cat(params byte[][] parts)
    {
        var result = new List<byte>();
        foreach (var p in parts) result.AddRange(p);
        return result.ToArray();
    }

    public static byte[] Empty => Array.Empty<byte>();

    #endregion

    #region Types (type.proto: Type oneof kind)

    // Type.Boolean=1, I8=2, I16=3, I32=5, I64=7, FP32=10, FP64=11, String=12, Date=16,
    // IntervalDay=20, Decimal=24, Uuid=32, PrecisionTimestamp=33, PrecisionTimestampTZ=34.
    // Simple types: nullability=2. Precision types: precision=1, nullability=3.
    // Decimal: scale=1, precision=2, nullability=4. Nullability: NULLABLE=1, REQUIRED=2.
    public static byte[] TBool => LenDel(1, VarField(2, 1));
    public static byte[] TI8 => LenDel(2, VarField(2, 1));
    public static byte[] TI16 => LenDel(3, VarField(2, 1));
    public static byte[] TFp32 => LenDel(10, VarField(2, 1));
    public static byte[] TBinary => LenDel(13, VarField(2, 1));
    public static byte[] TIntervalYear => LenDel(19, VarField(2, 1));
    public static byte[] TVarChar(int length) => LenDel(22, Cat(VarField(1, length), VarField(3, 1)));
    public static byte[] TTime(int precision = 6) => LenDel(36, Cat(VarField(1, precision), VarField(3, 1)));
    public static byte[] TI32 => LenDel(5, VarField(2, 1));
    public static byte[] TI64 => LenDel(7, VarField(2, 1));
    public static byte[] TFp64 => LenDel(11, VarField(2, 1));
    public static byte[] TString => LenDel(12, VarField(2, 1));
    public static byte[] TDate => LenDel(16, VarField(2, 1));
    public static byte[] TIntervalDay(int precision = 6) => LenDel(20, Cat(VarField(2, 1), VarField(3, precision)));
    public static byte[] TDecimal(int precision, int scale) => LenDel(24, Cat(VarField(1, scale), VarField(2, precision), VarField(4, 1)));
    public static byte[] TUuid => LenDel(32, VarField(2, 1));
    public static byte[] TTimestamp(int precision = 6) => LenDel(33, Cat(VarField(1, precision), VarField(3, 1)));
    public static byte[] TTimestampTz(int precision = 6) => LenDel(34, Cat(VarField(1, precision), VarField(3, 1)));
    public static byte[] TStruct(params byte[][] types) => LenDel(25, Cat(types.Select(t => LenDel(1, t)).ToArray()));

    #endregion

    #region Relations (algebra.proto)

    /// <summary>Rel{read=1: ReadRel{base_schema=2: NamedStruct{names=1, struct=2: Struct{types=1}}, named_table=7: NamedTable{names=1}}}</summary>
    public static byte[] Read(string table, params (string Name, byte[] Type)[] columns)
        => Read(new[] { table }, columns);

    public static byte[] Read(string[] tableNames, (string Name, byte[] Type)[] columns, byte[]? extra = null)
    {
        var schema = Cat(
            Cat(columns.Select(c => StrField(1, c.Name)).ToArray()),
            LenDel(2, Cat(columns.Select(c => LenDel(1, c.Type)).ToArray())));
        var namedTable = Cat(tableNames.Select(n => StrField(1, n)).ToArray());
        return LenDel(1, Cat(LenDel(2, schema), extra ?? Empty, LenDel(7, namedTable)));
    }

    /// <summary>
    /// Rel{read=1: ReadRel{base_schema=2, virtual_table=5: VirtualTable{expressions=2:
    /// Expression.Nested.Struct{fields=1}}}}
    /// </summary>
    public static byte[] Values((string Name, byte[] Type)[] columns, params byte[][][] rows)
    {
        var schema = Cat(
            Cat(columns.Select(c => StrField(1, c.Name)).ToArray()),
            LenDel(2, Cat(columns.Select(c => LenDel(1, c.Type)).ToArray())));
        var table = Cat(rows.Select(r => LenDel(2, Cat(r.Select(f => LenDel(1, f)).ToArray()))).ToArray());
        return LenDel(1, Cat(LenDel(2, schema), LenDel(5, table)));
    }

    /// <summary>ReadRel.filter=3</summary>
    public static byte[] ReadFilter(byte[] condition) => LenDel(3, condition);

    /// <summary>ReadRel.projection=4: MaskExpression{select=1: StructSelect{struct_items=1: StructItem{field=1}}}</summary>
    public static byte[] ReadProjection(params int[] fields)
        => LenDel(4, LenDel(1, Cat(fields.Select(f => LenDel(1, VarField(1, f))).ToArray())));

    /// <summary>RelCommon{emit=2: Emit{output_mapping=1 (packed)}}, in the common=1 field of a relation.</summary>
    public static byte[] Emit(params int[] mapping) => LenDel(1, LenDel(2, Packed(1, mapping)));

    /// <summary>Rel{filter=2: FilterRel{input=2, condition=3}}</summary>
    public static byte[] Filter(byte[] input, byte[] condition)
        => LenDel(2, Cat(LenDel(2, input), LenDel(3, condition)));

    /// <summary>Rel{project=7: ProjectRel{common=1, input=2, expressions=3}}</summary>
    public static byte[] Project(byte[] input, params byte[][] expressions) => ProjectEmit(input, null, expressions);

    public static byte[] ProjectEmit(byte[] input, int[]? emit, params byte[][] expressions)
        => LenDel(7, Cat(emit == null ? Empty : Emit(emit), LenDel(2, input), Cat(expressions.Select(e => LenDel(3, e)).ToArray())));

    /// <summary>Rel{fetch=3: FetchRel{input=2, offset_expr=5, count_expr=6}}</summary>
    public static byte[] Fetch(byte[] input, long? offset, long? count)
        => LenDel(3, Cat(LenDel(2, input),
            offset.HasValue ? LenDel(5, I64(offset.Value)) : Empty,
            count.HasValue ? LenDel(6, I64(count.Value)) : Empty));

    /// <summary>Legacy FetchRel{offset=3, count=4} (int64 fields, now reserved).</summary>
    public static byte[] FetchLegacy(byte[] input, long offset, long count)
        => LenDel(3, Cat(LenDel(2, input), VarField(3, offset), VarField(4, count)));

    public const int AscNullsFirst = 1, AscNullsLast = 2, DescNullsFirst = 3, DescNullsLast = 4, Clustered = 5;

    /// <summary>SortField{expr=1, direction=2}</summary>
    public static byte[] SortField(byte[] expr, int direction) => Cat(LenDel(1, expr), VarField(2, direction));

    /// <summary>Rel{sort=5: SortRel{input=2, sorts=3}}</summary>
    public static byte[] Sort(byte[] input, params byte[][] sortFields)
        => LenDel(5, Cat(LenDel(2, input), Cat(sortFields.Select(s => LenDel(3, s)).ToArray())));

    /// <summary>
    /// Rel{aggregate=4: AggregateRel{input=2, groupings=3: Grouping{expression_references=2},
    /// measures=4, grouping_expressions=5}}, with a single grouping set over all keys.
    /// </summary>
    public static byte[] Aggregate(byte[] input, byte[][] keys, params byte[][] measures)
    {
        var groupings = keys.Length == 0 ? Empty : LenDel(3, Packed(2, Enumerable.Range(0, keys.Length)));
        return LenDel(4, Cat(LenDel(2, input), groupings,
            Cat(measures.Select(m => LenDel(4, m)).ToArray()),
            Cat(keys.Select(k => LenDel(5, k)).ToArray())));
    }

    /// <summary>AggregateRel with explicit grouping sets: each set lists indexes into keys.</summary>
    public static byte[] AggregateSets(byte[] input, byte[][] keys, int[][] sets, params byte[][] measures)
        => LenDel(4, Cat(LenDel(2, input),
            Cat(sets.Select(s => LenDel(3, Packed(2, s))).ToArray()),
            Cat(measures.Select(m => LenDel(4, m)).ToArray()),
            Cat(keys.Select(k => LenDel(5, k)).ToArray())));

    /// <summary>Legacy AggregateRel grouping: Grouping{grouping_expressions=1} (now reserved).</summary>
    public static byte[] AggregateLegacy(byte[] input, byte[][] keys, params byte[][] measures)
        => LenDel(4, Cat(LenDel(2, input),
            LenDel(3, Cat(keys.Select(k => LenDel(1, k)).ToArray())),
            Cat(measures.Select(m => LenDel(4, m)).ToArray())));

    public const int InvocationAll = 1, InvocationDistinct = 2;

    /// <summary>
    /// Measure{measure=1: AggregateFunction{function_reference=1, arguments=7: FunctionArgument{value=3},
    /// sorts=3, phase=4, invocation=6, options=8}, filter=2}
    /// </summary>
    public static byte[] Measure(int function, byte[][] args, byte[]? filter = null, int invocation = InvocationAll,
        byte[]? extra = null)
        => Cat(
            LenDel(1, Cat(VarField(1, function), Cat(args.Select(a => LenDel(7, LenDel(3, a))).ToArray()),
                VarField(6, invocation), extra ?? Empty)),
            filter == null ? Empty : LenDel(2, filter));

    /// <summary>Measure with sort fields (AggregateFunction.sorts=3): an ordered aggregate.</summary>
    public static byte[] MeasureSorted(int function, byte[][] args, params byte[][] sortFields)
        => Measure(function, args, extra: Cat(sortFields.Select(s => LenDel(3, s)).ToArray()));

    /// <summary>Measure whose function has an enum first argument (FunctionArgument.enum=1).</summary>
    public static byte[] MeasureWithEnum(int function, string enumValue, params byte[][] args)
        => LenDel(1, Cat(VarField(1, function), LenDel(7, StrField(1, enumValue)),
            Cat(args.Select(a => LenDel(7, LenDel(3, a))).ToArray())));

    public const int Inner = 1, Outer = 2, Left = 3, Right = 4, LeftSemi = 5, LeftAnti = 6, LeftSingle = 7,
        RightSemi = 8, RightAnti = 9, RightSingle = 10, LeftMark = 11, RightMark = 12;

    /// <summary>Rel{join=6: JoinRel{left=2, right=3, expression=4, post_join_filter=5, type=6}}</summary>
    public static byte[] Join(byte[] left, byte[] right, byte[]? condition, int type, byte[]? postJoinFilter = null)
        => LenDel(6, Cat(LenDel(2, left), LenDel(3, right),
            condition == null ? Empty : LenDel(4, condition),
            postJoinFilter == null ? Empty : LenDel(5, postJoinFilter),
            VarField(6, type)));

    /// <summary>Rel{cross=12: CrossRel{left=2, right=3}}</summary>
    public static byte[] Cross(byte[] left, byte[] right) => LenDel(12, Cat(LenDel(2, left), LenDel(3, right)));

    public const int MinusPrimary = 1, MinusMultiset = 2, IntersectionPrimary = 3, IntersectionMultiset = 4,
        UnionDistinct = 5, UnionAll = 6, MinusPrimaryAll = 7, IntersectionMultisetAll = 8;

    /// <summary>Rel{set=8: SetRel{inputs=2, op=3}}</summary>
    public static byte[] Set(int op, params byte[][] inputs)
        => LenDel(8, Cat(Cat(inputs.Select(i => LenDel(2, i)).ToArray()), VarField(3, op)));

    /// <summary>Rel{top_n=23: TopNRel{input=2, sorts=3, offset=4, count=5}}</summary>
    public static byte[] TopN(byte[] input, long count, params byte[][] sortFields)
        => LenDel(23, Cat(LenDel(2, input), Cat(sortFields.Select(s => LenDel(3, s)).ToArray()), LenDel(5, I64(count))));

    public const int RowsOnly = 1, WithTies = 2;

    /// <summary>Rel{top_n=23: TopNRel{input=2, sorts=3, offset=4, count=5, mode=6}}</summary>
    public static byte[] TopN(byte[] input, long offset, long count, int mode, params byte[][] sortFields)
        => LenDel(23, Cat(LenDel(2, input), Cat(sortFields.Select(s => LenDel(3, s)).ToArray()),
            LenDel(4, I64(offset)), LenDel(5, I64(count)), VarField(6, mode)));

    #endregion

    #region Expressions (algebra.proto: Expression oneof rex_type)

    /// <summary>Expression{selection=2: FieldReference{direct_reference=1: ReferenceSegment{struct_field=2: {field=1}}, root_reference=4}}</summary>
    public static byte[] Field(int index)
        => LenDel(2, Cat(LenDel(1, LenDel(2, VarField(1, index))), LenDel(4, Empty)));

    /// <summary>
    /// Expression{selection=2: FieldReference{direct_reference=1, outer_reference=5: OuterReference{steps_out=1}}}:
    /// a field of the input of the relation <paramref name="stepsOut"/> subqueries out.
    /// </summary>
    public static byte[] OuterField(int index, int stepsOut = 1)
        => LenDel(2, Cat(LenDel(1, LenDel(2, VarField(1, index))), LenDel(5, VarField(1, stepsOut))));

    // Expression{literal=1: Literal{...}}: boolean=1, i8=2, i32=5, i64=7, fp64=11, string=12,
    // date=16, interval_day_to_second=20, decimal=24, uuid=28, null=29,
    // precision_timestamp=34, precision_timestamp_tz=35.
    static byte[] Lit(byte[] literal) => LenDel(1, literal);
    public static byte[] Bool(bool v) => Lit(VarField(1, v ? 1 : 0));
    public static byte[] I8(int v) => Lit(VarField(2, v));
    public static byte[] I16(int v) => Lit(VarField(3, v));
    public static byte[] I32(int v) => Lit(VarField(5, v));
    public static byte[] Fp32(float v) => Lit(Cat(Tag(10, 5), BitConverter.GetBytes(v)));
    public static byte[] Binary(params byte[] v) => Lit(LenDel(13, v));
    public static byte[] Time(int precision, long value) => Lit(LenDel(37, Cat(VarField(1, precision), VarField(2, value))));
    public static byte[] IntervalYear(int years, int months) => Lit(LenDel(19, Cat(VarField(1, years), VarField(2, months))));
    public static byte[] I64(long v) => Lit(VarField(7, v));
    public static byte[] Fp64(double v) => Lit(Fixed64Field(11, BitConverter.DoubleToInt64Bits(v)));
    public static byte[] Str(string v) => Lit(StrField(12, v));
    public static byte[] Date(int daysSinceEpoch) => Lit(VarField(16, daysSinceEpoch));
    public static byte[] Null(byte[] type) => Lit(LenDel(29, type));
    public static byte[] Timestamp(int precision, long value) => Lit(LenDel(34, Cat(VarField(1, precision), VarField(2, value))));
    public static byte[] TimestampTz(int precision, long value) => Lit(LenDel(35, Cat(VarField(1, precision), VarField(2, value))));
    public static byte[] IntervalDay(int days, int seconds, long subseconds = 0, int precision = 6)
        => Lit(LenDel(20, Cat(VarField(1, days), VarField(2, seconds), VarField(4, precision), VarField(5, subseconds))));
    public static byte[] Uuid(Guid g) => Lit(LenDel(28, GuidBytesBigEndian(g)));
    public static byte[] Decimal(System.Numerics.BigInteger unscaled, int precision, int scale)
    {
        var bytes = new byte[16];
        var raw = unscaled.ToByteArray();
        byte fill = unscaled.Sign < 0 ? (byte)0xFF : (byte)0;
        for (int i = 0; i < 16; i++) bytes[i] = i < raw.Length ? raw[i] : fill;
        return Lit(LenDel(24, Cat(LenDel(1, bytes), VarField(2, precision), VarField(3, scale))));
    }

    static byte[] GuidBytesBigEndian(Guid g)
    {
        var s = g.ToString("N");
        var bytes = new byte[16];
        for (int i = 0; i < 16; i++) bytes[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return bytes;
    }

    /// <summary>
    /// Expression{scalar_function=3: ScalarFunction{function_reference=1, arguments=4: FunctionArgument{value=3}, options=5}}
    /// </summary>
    public static byte[] Call(int function, params byte[][] args) => CallWith(function, null, args);

    /// <summary>Scalar function with FunctionOption{name=1, preference=2} options.</summary>
    public static byte[] CallWith(int function, (string Name, string[] Preference)[]? options, params byte[][] args)
        => LenDel(3, Cat(VarField(1, function),
            Cat(args.Select(a => LenDel(4, a.Length > 0 && a[0] == EnumMarker ? a.Skip(1).ToArray() : LenDel(3, a))).ToArray()),
            options == null ? Empty : Cat(options.Select(o => LenDel(5, Cat(StrField(1, o.Name), Cat(o.Preference.Select(p => StrField(2, p)).ToArray())))).ToArray())));

    const byte EnumMarker = 0xFF;

    /// <summary>An enumeration argument (FunctionArgument.enum=1) for <see cref="Call"/>.</summary>
    public static byte[] Enum(string value) => Cat(new[] { EnumMarker }, StrField(1, value));

    /// <summary>Expression{if_then=6: IfThen{ifs=1: IfClause{if=1, then=2}, else=2}}</summary>
    public static byte[] IfThen((byte[] If, byte[] Then)[] clauses, byte[]? @else)
        => LenDel(6, Cat(Cat(clauses.Select(c => LenDel(1, Cat(LenDel(1, c.If), LenDel(2, c.Then)))).ToArray()),
            @else == null ? Empty : LenDel(2, @else)));

    /// <summary>Expression{switch_expression=7: SwitchExpression{match=3, ifs=1: IfValue{if=1 (Literal), then=2}, else=2}}</summary>
    public static byte[] Switch(byte[] match, (byte[] Literal, byte[] Then)[] cases, byte[]? @else)
        => LenDel(7, Cat(LenDel(3, match),
            Cat(cases.Select(c => LenDel(1, Cat(LenDel(1, StripExpression(c.Literal)), LenDel(2, c.Then)))).ToArray()),
            @else == null ? Empty : LenDel(2, @else)));

    /// <summary>Extracts the Literal message from an Expression{literal=1: ...} built by this class.</summary>
    static byte[] StripExpression(byte[] literalExpression)
    {
        int pos = 1; // tag byte for field 1, wire type 2
        ulong len = 0;
        int shift = 0;
        byte b;
        do { b = literalExpression[pos++]; len |= (ulong)(b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0);
        return literalExpression.Skip(pos).Take((int)len).ToArray();
    }

    /// <summary>Expression{singular_or_list=8: SingularOrList{value=1, options=2}}</summary>
    public static byte[] InList(byte[] value, params byte[][] options)
        => LenDel(8, Cat(LenDel(1, value), Cat(options.Select(o => LenDel(2, o)).ToArray())));

    /// <summary>Expression{multi_or_list=9: MultiOrList{value=1, options=2: Record{fields=1}}}</summary>
    public static byte[] MultiInList(byte[][] values, params byte[][][] records)
        => LenDel(9, Cat(Cat(values.Select(v => LenDel(1, v)).ToArray()),
            Cat(records.Select(r => LenDel(2, Cat(r.Select(f => LenDel(1, f)).ToArray()))).ToArray())));

    /// <summary>Expression{subquery=12: Subquery{scalar=1: Scalar{input=1}}}</summary>
    public static byte[] ScalarSubquery(byte[] rel) => LenDel(12, LenDel(1, LenDel(1, rel)));

    /// <summary>Expression{subquery=12: Subquery{in_predicate=2: InPredicate{needles=1, haystack=2}}}</summary>
    public static byte[] InSubquery(byte[] needle, byte[] rel) => LenDel(12, LenDel(2, Cat(LenDel(1, needle), LenDel(2, rel))));

    /// <summary>Expression{subquery=12: Subquery{set_predicate=3: SetPredicate{predicate_op=1 (EXISTS=1), tuples=2}}}</summary>
    public static byte[] Exists(byte[] rel) => LenDel(12, LenDel(3, Cat(VarField(1, 1), LenDel(2, rel))));

    public const int Eq = 1, Ne = 2, Lt = 3, Gt = 4, Le = 5, Ge = 6, Any = 1, All = 2;

    /// <summary>Expression{subquery=12: Subquery{set_comparison=4: SetComparison{reduction_op=1, comparison_op=2, left=3, right=4}}}</summary>
    public static byte[] SetComparison(byte[] left, int comparison, int reduction, byte[] rel)
        => LenDel(12, LenDel(4, Cat(VarField(1, reduction), VarField(2, comparison), LenDel(3, left), LenDel(4, rel))));

    public const int FailureUnspecified = 0, FailureReturnNull = 1, FailureThrow = 2;

    /// <summary>Expression{cast=11: Cast{type=1, input=2, failure_behavior=3}}</summary>
    public static byte[] Cast(byte[] type, byte[] input, int failure = FailureUnspecified)
        => LenDel(11, Cat(LenDel(1, type), LenDel(2, input), VarField(3, failure)));

    /// <summary>Expression{execution_context_variable=17: {current_timestamp=1 | current_timezone=2 | current_date=3}}</summary>
    public static byte[] ContextVariable(int which) => LenDel(17, LenDel(which, Empty));

    #endregion

    #region Plans (plan.proto)

    /// <summary>
    /// Plan{extension_urns=8: SimpleExtensionURN{extension_urn_anchor=1, urn=2},
    /// extensions=2: SimpleExtensionDeclaration{extension_function=3: {extension_urn_reference=4, function_anchor=2, name=3}},
    /// relations=3: PlanRel{root=2: RelRoot{input=1, names=2}}}
    /// </summary>
    public static byte[] Plan(byte[] rel, string[]? names = null, Functions? functions = null, byte[]? extra = null)
    {
        var root = Cat(LenDel(1, rel), Cat((names ?? Array.Empty<string>()).Select(n => StrField(2, n)).ToArray()));
        return Cat(functions?.Encode() ?? Empty, extra ?? Empty, LenDel(3, LenDel(2, root)));
    }

    /// <summary>Plan{relations=3: PlanRel{rel=1: Rel}} without a RelRoot.</summary>
    public static byte[] PlanBareRel(byte[] rel, Functions? functions = null)
        => Cat(functions?.Encode() ?? Empty, LenDel(3, LenDel(1, rel)));

    #endregion
}

/// <summary>Collects the extension function declarations of a test plan.</summary>
sealed class Functions
{
    const string StandardPrefix = "extension:io.substrait:";
    readonly List<string> _urns = new();
    readonly List<(int Anchor, int? UrnAnchor, string Name)> _functions = new();
    bool _legacyUris;

    /// <summary>Declares a function from a standard extension (e.g. "functions_arithmetic", "add:i64_i64").</summary>
    public int Add(string extension, string compoundName)
    {
        string urn = extension.Contains(":") ? extension : StandardPrefix + extension;
        int urnIndex = _urns.IndexOf(urn);
        if (urnIndex < 0)
        {
            _urns.Add(urn);
            urnIndex = _urns.Count - 1;
        }
        int anchor = _functions.Count + 1;
        _functions.Add((anchor, urnIndex + 1, compoundName));
        return anchor;
    }

    /// <summary>Declares a function without naming its extension (as some producers do).</summary>
    public int AddBare(string name)
    {
        int anchor = _functions.Count + 1;
        _functions.Add((anchor, null, name));
        return anchor;
    }

    /// <summary>Encode extensions as legacy URIs (Plan.extension_uris=1, ExtensionFunction.extension_uri_reference=1).</summary>
    public Functions UseLegacyUris()
    {
        _legacyUris = true;
        return this;
    }

    public byte[] Encode()
    {
        var parts = new List<byte[]>();
        for (int i = 0; i < _urns.Count; i++)
        {
            string text = _legacyUris
                ? "https://github.com/substrait-io/substrait/blob/main/extensions/" + _urns[i].Substring(StandardPrefix.Length) + ".yaml"
                : _urns[i];
            parts.Add(P.LenDel(_legacyUris ? 1 : 8, P.Cat(P.VarField(1, i + 1), P.StrField(2, text))));
        }
        foreach (var (anchor, urnAnchor, name) in _functions)
        {
            var fn = P.Cat(
                urnAnchor.HasValue ? P.VarField(_legacyUris ? 1 : 4, urnAnchor.Value) : P.Empty,
                P.VarField(2, anchor),
                P.StrField(3, name));
            parts.Add(P.LenDel(2, P.LenDel(3, fn)));
        }
        return P.Cat(parts.ToArray());
    }
}
