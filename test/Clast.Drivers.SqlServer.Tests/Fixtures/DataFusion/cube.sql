SELECT customer_id, flag, count(*) AS n FROM orders GROUP BY CUBE (customer_id, flag)
