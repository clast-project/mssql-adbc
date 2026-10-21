SELECT o.id AS oid, c.id AS cid FROM orders o JOIN customers c ON o.customer_id = c.id
