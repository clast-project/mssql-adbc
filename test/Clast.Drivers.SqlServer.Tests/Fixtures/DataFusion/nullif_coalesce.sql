SELECT nullif(name, 'a') AS n, coalesce(name, 'none') AS c FROM orders
