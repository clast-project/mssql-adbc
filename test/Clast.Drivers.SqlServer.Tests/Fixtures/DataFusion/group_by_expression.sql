SELECT qty % 2 AS parity, count(*) AS n FROM orders GROUP BY qty % 2
