"""Generate Substrait plan fixtures from SQL with DataFusion.

Usage:
    python scripts/gen_datafusion_fixtures.py test/Clast.Drivers.SqlServer.Tests/Fixtures/DataFusion

For each query, writes <name>.sql (the query), <name>.pb (DataFusion's Substrait plan),
<name>.json (a readable rendering of the plan) and <name>.expected.json (DataFusion's own
result over the test data). data.json holds that data and the SQL Server column types the
tests create temporary tables with, so DataFusion and SQL Server see the same rows.

The tests translate each plan to T-SQL, compare the translation with a reviewed snapshot
(<name>.tsql) and, given a server, compare its result with DataFusion's.

Requires: pip install datafusion substrait protobuf pyarrow
"""
import datetime
import decimal
import json
import math
import os
import sys

import datafusion
import pyarrow as pa
from datafusion import SessionContext
from datafusion import substrait as ss
from google.protobuf import json_format
from substrait import plan_pb2


def ts(text):
    return datetime.datetime.fromisoformat(text)


def day(text):
    return datetime.date.fromisoformat(text)


D = decimal.Decimal

# Edge cases: null keys and values on both sides, duplicate keys, negative numbers, strings that
# differ only in case (SQL Server collations often ignore case), a leap day.
# Each column: (name, arrow type, SQL Server type).
TABLES = {
    "orders": {
        "columns": [
            ("id", pa.int64(), "bigint NOT NULL"),
            ("customer_id", pa.int64(), "bigint"),
            ("name", pa.string(), "nvarchar(50)"),
            ("price", pa.float64(), "float"),
            ("qty", pa.int32(), "int"),
            ("ts", pa.timestamp("us"), "datetime2(6)"),
            ("flag", pa.bool_(), "bit"),
            ("amount", pa.decimal128(10, 2), "decimal(10,2)"),
            ("d", pa.date32(), "date"),
        ],
        "rows": [
            (1, 10, "apple", 3.0, 6, ts("2023-12-31T23:59:59"), True, D("12.50"), day("2023-12-31")),
            (2, 10, "banana", None, 1, ts("2024-01-01T00:00:00"), False, D("0.99"), day("2024-01-01")),
            (3, 20, "Avocado", 7.5, None, ts("2024-03-15T12:30:00.123456"), None, None, day("2024-03-15")),
            (4, None, "cherry", 2.25, 9, None, True, D("100.00"), None),
            (5, 30, "apricot", 3.0, -3, ts("2024-06-01T00:00:00"), False, D("-5.25"), day("2024-06-02")),
            (6, None, "date", -1.5, 2, ts("2025-01-01T00:00:00"), True, D("7.00"), day("2025-01-01")),
            (7, 40, "a.b", 100.0, 7, ts("2022-07-04T08:00:00"), None, D("3.33"), day("2022-07-04")),
            (8, 20, None, 4.0, 3, ts("2024-02-29T10:00:00"), False, D("1.10"), day("2024-02-29")),
            (9, 30, "Apple", 3.0, 1, ts("2021-01-03T00:00:00"), True, D("2.00"), day("2021-01-03")),
        ],
    },
    "customers": {
        "columns": [
            ("id", pa.int64(), "bigint"),
            ("cname", pa.string(), "nvarchar(50) NOT NULL"),
            ("region", pa.string(), "nvarchar(10) NOT NULL"),
        ],
        "rows": [
            (10, "x", "r"),
            (20, "y", "s"),
            (None, "z", "r"),
            (50, "w", "r"),
            (30, "X", "S"),
        ],
    },
}

QUERIES = {
    # From the Kusto driver's fixtures.
    "filter_project": "SELECT name, price * 2 AS double_price FROM orders WHERE qty > 5",
    "aggregate_sort_limit": "SELECT name, sum(price) AS total FROM orders WHERE qty > 5 GROUP BY name ORDER BY total DESC LIMIT 10",
    "arithmetic": "SELECT id + 1 AS a, price * 2.5 AS b, qty / 2 AS c, id % 3 AS d, -price AS e FROM orders",
    "like_between": "SELECT id FROM orders WHERE name LIKE 'a%' AND price BETWEEN 1 AND 5",
    "in_list": "SELECT id FROM orders WHERE name IN ('a', 'b') OR id IN (1, 2, 3)",
    "case_when": "SELECT CASE WHEN qty > 1 THEN 'many' WHEN qty = 1 THEN 'one' ELSE 'none' END AS c FROM orders",
    "cast": "SELECT CAST(qty AS BIGINT) AS q, CAST(price AS VARCHAR) AS p FROM orders",
    "is_null": "SELECT id FROM orders WHERE name IS NULL OR price IS NOT NULL",
    "string_functions": "SELECT upper(name) AS u, lower(name) AS l, substr(name, 2, 3) AS s, replace(name, 'a', 'b') AS r FROM orders",
    "string_predicates": "SELECT id FROM orders WHERE starts_with(name, 'a') OR ends_with(name, 'z') OR strpos(name, 'q') > 0",
    "string_length": "SELECT length(name) AS n FROM orders",
    "aggregates": "SELECT count(*) AS n, count(name) AS nn, count(DISTINCT name) AS nd, avg(price) AS a, min(price) AS lo, max(id) AS hi FROM orders",
    "stddev": "SELECT stddev(price) AS s FROM orders",
    "inner_join": "SELECT o.id, c.cname FROM orders o JOIN customers c ON o.customer_id = c.id",
    "left_join_extra_condition": "SELECT o.id, c.cname FROM orders o LEFT JOIN customers c ON o.customer_id = c.id AND c.region = 'r'",
    "join_same_column_names": "SELECT o.id AS oid, c.id AS cid FROM orders o JOIN customers c ON o.customer_id = c.id",
    "exists_semi_join": "SELECT id FROM orders o WHERE EXISTS (SELECT 1 FROM customers c WHERE c.id = o.customer_id)",
    "not_in_anti_join": "SELECT id FROM orders o WHERE customer_id NOT IN (SELECT id FROM customers)",
    "union_all": "SELECT name FROM orders UNION ALL SELECT cname FROM customers",
    "union_distinct": "SELECT name FROM orders UNION SELECT cname FROM customers",
    "distinct": "SELECT DISTINCT name FROM orders",
    "order_limit_offset": "SELECT id, name FROM orders ORDER BY price ASC, name DESC LIMIT 5 OFFSET 2",
    "timestamp_literal": "SELECT id FROM orders WHERE ts > TIMESTAMP '2024-01-01 00:00:00'",
    "date_part": "SELECT extract(year FROM ts) AS y FROM orders",
    "not_and_not_equal": "SELECT id FROM orders WHERE NOT flag AND price <> 3",
    "filtered_aggregate": "SELECT sum(price) FILTER (WHERE qty > 1) AS s FROM orders",
    "rollup": "SELECT name, count(*) AS n FROM orders GROUP BY ROLLUP(name)",
    "math": "SELECT abs(price) AS a, round(price, 2) AS r, ceil(price) AS c, floor(price) AS f, sqrt(abs(price)) AS s, power(price, 2.0) AS p FROM orders",
    "concat": "SELECT concat(name, 'x') AS c FROM orders",
    "cross_join": "SELECT o.id, c.cname FROM orders o, customers c",
    "group_by_expression": "SELECT qty % 2 AS parity, count(*) AS n FROM orders GROUP BY qty % 2",
    "nullif_coalesce": "SELECT nullif(name, 'a') AS n, coalesce(name, 'none') AS c FROM orders",
    "having": "SELECT customer_id, sum(price) AS total FROM orders GROUP BY customer_id HAVING sum(price) > 5",
    "sort_by_expression": "SELECT id FROM orders ORDER BY price * qty DESC, id",
    "multi_join": "SELECT o.id, c.cname, c2.region FROM orders o JOIN customers c ON o.customer_id = c.id JOIN customers c2 ON c.id = c2.id",
    # SQL Server specifics: collations, null ordering, set operations, outer joins, subqueries.
    "case_sensitive_group": "SELECT name, count(*) AS n FROM orders GROUP BY name ORDER BY name",
    "string_ordering": "SELECT name FROM orders WHERE name > 'Apple' ORDER BY name",
    "order_nulls_last": "SELECT id, price FROM orders ORDER BY price ASC NULLS LAST, id",
    "order_desc_nulls_first": "SELECT id, qty FROM orders ORDER BY qty DESC NULLS FIRST, id",
    "grouping_sets": "SELECT customer_id, qty % 2 AS parity, count(*) AS n FROM orders GROUP BY GROUPING SETS ((customer_id), (qty % 2), ())",
    "cube": "SELECT customer_id, flag, count(*) AS n FROM orders GROUP BY CUBE (customer_id, flag)",
    "intersect": "SELECT customer_id FROM orders INTERSECT SELECT id FROM customers",
    "except": "SELECT customer_id FROM orders EXCEPT SELECT id FROM customers",
    "intersect_all": "SELECT customer_id FROM orders INTERSECT ALL SELECT id FROM customers",
    "except_all": "SELECT customer_id FROM orders EXCEPT ALL SELECT id FROM customers",
    "union_three": "SELECT id FROM orders WHERE qty > 5 UNION ALL SELECT id FROM customers UNION ALL SELECT 99",
    "full_join": "SELECT o.id AS oid, c.id AS cid FROM orders o FULL JOIN customers c ON o.customer_id = c.id",
    "right_join": "SELECT o.id AS oid, c.cname FROM orders o RIGHT JOIN customers c ON o.customer_id = c.id",
    "left_join_coalesce": "SELECT o.id, coalesce(c.cname, 'none') AS cname FROM orders o LEFT JOIN customers c ON o.customer_id = c.id",
    "in_subquery": "SELECT id FROM orders WHERE customer_id IN (SELECT id FROM customers WHERE region = 'r')",
    "scalar_subquery": "SELECT id, price - (SELECT avg(price) FROM orders) AS diff FROM orders",
    "correlated_scalar_subquery": "SELECT id, (SELECT max(c.cname) FROM customers c WHERE c.id = o.customer_id) AS cname FROM orders o",
    "not_exists": "SELECT id FROM orders o WHERE NOT EXISTS (SELECT 1 FROM customers c WHERE c.id = o.customer_id)",
    "values": "SELECT x, y FROM (VALUES (1, 'a'), (2, 'b'), (3, NULL)) AS t(x, y)",
    "decimal_arithmetic": "SELECT id, amount * 2 AS twice, amount + amount AS sum2 FROM orders",
    "decimal_aggregates": "SELECT sum(amount) AS s, min(amount) AS lo, max(amount) AS hi FROM orders",
    "date_filter": "SELECT id FROM orders WHERE d >= DATE '2024-01-01' AND d < DATE '2025-01-01'",
    "boolean_projection": "SELECT id, flag, NOT flag AS nf, flag AND qty > 2 AS both FROM orders",
    "is_not_distinct_from": "SELECT id FROM orders WHERE customer_id IS NOT DISTINCT FROM NULL",
    "count_distinct_filter": "SELECT count(DISTINCT customer_id) FILTER (WHERE qty > 0) AS n FROM orders",
    "having_count": "SELECT customer_id, count(*) AS n FROM orders GROUP BY customer_id HAVING count(*) > 1",
    "negative_modulus": "SELECT id, qty % 4 AS m FROM orders",
    "limit": "SELECT id FROM orders ORDER BY id LIMIT 3",
    "nested_case": "SELECT id, CASE WHEN price IS NULL THEN 'none' WHEN price > 5 THEN CASE WHEN qty > 5 THEN 'big' ELSE 'pricey' END ELSE 'cheap' END AS c FROM orders",
    "window_row_number": "SELECT id, row_number() OVER (ORDER BY id) AS rn FROM orders",
    "trim": "SELECT trim(name) AS t FROM orders",
}


def normalize(value, arrow_type):
    if value is None:
        return None
    if pa.types.is_boolean(arrow_type):
        return bool(value)
    if pa.types.is_integer(arrow_type):
        return int(value)
    if pa.types.is_floating(arrow_type):
        return None if math.isnan(value) else float(value)
    if pa.types.is_decimal(arrow_type):
        return format(value.normalize(), "f")
    if pa.types.is_timestamp(arrow_type):
        return value.strftime("%Y-%m-%dT%H:%M:%S.%f")
    if pa.types.is_date(arrow_type):
        return value.isoformat()
    if pa.types.is_string(arrow_type) or pa.types.is_large_string(arrow_type) or pa.types.is_string_view(arrow_type):
        return str(value)
    raise TypeError(f"unexpected result type {arrow_type}")


def context():
    c = SessionContext()
    for name, table in TABLES.items():
        columns = table["columns"]
        arrays = {col: pa.array([row[i] for row in table["rows"]], arrow_type) for i, (col, arrow_type, _) in enumerate(columns)}
        c.register_record_batches(name, [pa.table(arrays).to_batches()])
    return c


def data_json():
    def value(v):
        if isinstance(v, datetime.datetime):
            return v.strftime("%Y-%m-%dT%H:%M:%S.%f")
        if isinstance(v, datetime.date):
            return v.isoformat()
        if isinstance(v, D):
            return str(v)
        return v
    return {
        name: {
            "columns": [{"name": n, "sql": sql} for n, _, sql in table["columns"]],
            "rows": [[value(v) for v in row] for row in table["rows"]],
        }
        for name, table in TABLES.items()
    }


def main(out):
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, "data.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(data_json(), f, indent=1)
        f.write("\n")
    c = context()
    for name, sql in QUERIES.items():
        try:
            plan = ss.Serde.serialize_bytes(sql, c)
        except Exception as e:
            print(f"{name}: PRODUCER ERROR {str(e).splitlines()[0][:150]}")
            continue
        p = plan_pb2.Plan()
        p.ParseFromString(plan)
        table = c.sql(sql).to_arrow_table()
        rows = [[normalize(v, field.type) for v, field in zip(row, table.schema)]
                for row in zip(*[col.to_pylist() for col in table.columns])] if table.num_columns else []
        expected = {"ordered": "ORDER BY" in sql.upper(), "columns": table.schema.names, "rows": rows}
        with open(os.path.join(out, name + ".pb"), "wb") as f:
            f.write(plan)
        with open(os.path.join(out, name + ".json"), "w", encoding="utf-8", newline="\n") as f:
            f.write(json_format.MessageToJson(p, indent=1) + "\n")
        with open(os.path.join(out, name + ".sql"), "w", encoding="utf-8", newline="\n") as f:
            f.write(sql + "\n")
        with open(os.path.join(out, name + ".expected.json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump(expected, f, indent=1)
            f.write("\n")
        print(f"{name}: {len(plan)} bytes, {len(rows)} rows")
    with open(os.path.join(out, "README.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write(
            "# DataFusion plan fixtures\n\n"
            f"Substrait plans produced by DataFusion {datafusion.__version__} (Python bindings) from the SQL in each `.sql` file.\n"
            "`.pb` is the binary plan, `.json` a readable rendering, `.expected.json` DataFusion's result over the rows in\n"
            "`data.json`, and `.tsql` the reviewed translation (or `ERROR: <message fragment>` when the plan should be rejected).\n\n"
            "Regenerate the plans with `scripts/gen_datafusion_fixtures.py`; regenerate the `.tsql` snapshots by running\n"
            "the tests with `MSSQL_UPDATE_SNAPSHOTS=1` and reviewing the diff.\n")


if __name__ == "__main__":
    main(sys.argv[1])
