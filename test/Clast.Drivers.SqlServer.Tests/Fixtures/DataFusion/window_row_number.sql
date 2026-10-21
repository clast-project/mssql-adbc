SELECT id, row_number() OVER (ORDER BY id) AS rn FROM orders
