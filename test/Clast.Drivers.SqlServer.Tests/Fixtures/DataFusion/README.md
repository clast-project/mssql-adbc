# DataFusion plan fixtures

Substrait plans produced by DataFusion 55.0.0 (Python bindings) from the SQL in each `.sql` file.
`.pb` is the binary plan, `.json` a readable rendering, `.expected.json` DataFusion's result over the rows in
`data.json`, and `.tsql` the reviewed translation (or `ERROR: <message fragment>` when the plan should be rejected).

Regenerate the plans with `scripts/gen_datafusion_fixtures.py`; regenerate the `.tsql` snapshots by running
the tests with `MSSQL_UPDATE_SNAPSHOTS=1` and reviewing the diff.
