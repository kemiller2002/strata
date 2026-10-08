CREATE INDEX waiting_ticket ON ticket (status) WHERE status IN ('pending', 'on hold');
