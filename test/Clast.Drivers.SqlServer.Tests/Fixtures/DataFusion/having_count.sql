SELECT customer_id, count(*) AS n FROM orders GROUP BY customer_id HAVING count(*) > 1
