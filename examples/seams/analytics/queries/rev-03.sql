SELECT account_code, sum(total) AS revenue
FROM analytics.v_order_revenue
GROUP BY account_code;
