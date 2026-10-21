SELECT o.id, coalesce(c.cname, 'none') AS cname FROM orders o LEFT JOIN customers c ON o.customer_id = c.id
