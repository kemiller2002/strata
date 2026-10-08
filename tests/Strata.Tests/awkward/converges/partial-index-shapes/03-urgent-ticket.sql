CREATE UNIQUE INDEX urgent_ticket ON ticket (priority) WHERE priority > 5 AND closed_at IS NULL;
