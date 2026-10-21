SELECT count(*) AS n, count(name) AS nn, count(DISTINCT name) AS nd, avg(price) AS a, min(price) AS lo, max(id) AS hi FROM orders
