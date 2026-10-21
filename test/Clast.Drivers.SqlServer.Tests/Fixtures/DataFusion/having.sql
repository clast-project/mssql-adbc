SELECT customer_id, sum(price) AS total FROM orders GROUP BY customer_id HAVING sum(price) > 5
