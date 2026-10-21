SELECT id, price - (SELECT avg(price) FROM orders) AS diff FROM orders
