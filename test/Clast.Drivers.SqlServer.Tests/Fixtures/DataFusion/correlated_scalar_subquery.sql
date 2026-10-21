SELECT id, (SELECT max(c.cname) FROM customers c WHERE c.id = o.customer_id) AS cname FROM orders o
