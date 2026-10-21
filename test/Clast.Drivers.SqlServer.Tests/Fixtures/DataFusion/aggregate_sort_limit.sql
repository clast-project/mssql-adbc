SELECT name, sum(price) AS total FROM orders WHERE qty > 5 GROUP BY name ORDER BY total DESC LIMIT 10
