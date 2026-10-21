SELECT id FROM orders o WHERE customer_id NOT IN (SELECT id FROM customers)
