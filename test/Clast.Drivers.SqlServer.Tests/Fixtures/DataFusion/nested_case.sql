SELECT id, CASE WHEN price IS NULL THEN 'none' WHEN price > 5 THEN CASE WHEN qty > 5 THEN 'big' ELSE 'pricey' END ELSE 'cheap' END AS c FROM orders
