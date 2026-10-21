SELECT name, count(*) AS n FROM orders GROUP BY ROLLUP(name)
