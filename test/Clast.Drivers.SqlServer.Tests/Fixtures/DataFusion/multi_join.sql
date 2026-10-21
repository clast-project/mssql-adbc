SELECT o.id, c.cname, c2.region FROM orders o JOIN customers c ON o.customer_id = c.id JOIN customers c2 ON c.id = c2.id
