# Data Cleaning Summary

## Source Files Loaded
- transactions.csv: 929135 rows
- clients.csv: 1000 rows
- categories.csv: 29 rows
- subscriptions.csv: 1308 rows

## Cleaning Performed
- Standardized column names to snake_case, including mapping the unnamed transaction column to transaction_id.
- Trimmed whitespace across loaded values.
- Removed duplicate keys from lookup tables and duplicate transaction_id records from transactions.
- Removed rows with missing or invalid transaction amount.
- Converted transaction, birthdate, registration, and subscription dates to datetime during processing.
- Removed PII from the analytics output: fullname, address, phone/phone_number, and email.
- Imputed missing income with median income (201250) and missing expenses with median expenses (82114).
- Filled missing gender with Unknown and product/category fallbacks with Unknown where necessary.

## Merge Logic
- transactions.client_id left joined to clients.id.
- transactions.product_category left joined to categories.id.
- subscriptions.client_id joined through clients.id and evaluated as active on each transaction_date to preserve one row per transaction.

## Feature Engineering
- Created age, age_group, month, day_of_week, hour, income_minus_expenses, customer_segment, and subscription_status.
- Simulated bank_name, payment_gateway, payment_mode, transaction_status, processing_time_ms, retry_count, error_code, city/state, device_type, and network_type using a fixed random seed for reproducibility.

## Row Outcomes
- Final dataset rows: 929135
- Duplicate transaction_id rows removed: 0
- Rows removed for missing/invalid amount: 0
- Rows removed for invalid transaction_date: 0
- Duplicate client ids skipped: 0
- Duplicate category ids skipped: 0
- Duplicate subscription ids skipped: 0

## Simulated Transaction Status Distribution
- Failed: 37194
- Pending: 9365
- Success: 882576

## Validation Results
- Duplicate transaction_id count in final dataset: 0
- Success rows with non-null error_code: 0
- Failed rows missing error_code: 0
- Retry/status logic violations: 0
- Negative processing_time_ms rows: 0

## Output Files
- payment_operations_dataset.csv
- data_dictionary.csv
- data_cleaning_summary.md
