SELECT o.id AS oid, c.cname FROM orders o RIGHT JOIN customers c ON o.customer_id = c.id
