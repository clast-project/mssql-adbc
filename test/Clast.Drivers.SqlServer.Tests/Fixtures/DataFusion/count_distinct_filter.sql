SELECT count(DISTINCT customer_id) FILTER (WHERE qty > 0) AS n FROM orders
