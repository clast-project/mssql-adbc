SELECT customer_id, qty % 2 AS parity, count(*) AS n FROM orders GROUP BY GROUPING SETS ((customer_id), (qty % 2), ())
