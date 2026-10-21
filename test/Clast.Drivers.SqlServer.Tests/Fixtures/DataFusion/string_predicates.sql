SELECT id FROM orders WHERE starts_with(name, 'a') OR ends_with(name, 'z') OR strpos(name, 'q') > 0
