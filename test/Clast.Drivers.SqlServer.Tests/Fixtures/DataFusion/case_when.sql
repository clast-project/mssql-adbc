SELECT CASE WHEN qty > 1 THEN 'many' WHEN qty = 1 THEN 'one' ELSE 'none' END AS c FROM orders
