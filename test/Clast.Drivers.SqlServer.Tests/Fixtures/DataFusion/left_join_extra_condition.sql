SELECT o.id, c.cname FROM orders o LEFT JOIN customers c ON o.customer_id = c.id AND c.region = 'r'
