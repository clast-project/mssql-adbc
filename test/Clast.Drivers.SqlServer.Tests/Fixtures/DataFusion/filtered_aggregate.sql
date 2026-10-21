SELECT sum(price) FILTER (WHERE qty > 1) AS s FROM orders
