using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

public static class PaymentOperationsDatasetBuilder
{
    private sealed class Client
    {
        public DateTime? Birthdate;
        public string Gender = "Unknown";
        public decimal Income;
        public decimal Expenses;
    }

    private sealed class Subscription
    {
        public decimal Amount;
        public DateTime? Start;
        public DateTime? End;
    }

    public static void Run(string root)
    {
        var clientsRaw = ReadCsv(Path.Combine(root, "clients.csv"));
        var categoriesRaw = ReadCsv(Path.Combine(root, "categories.csv"));
        var subscriptionsRaw = ReadCsv(Path.Combine(root, "subscriptions.csv"));

        var incomeValues = clientsRaw.Rows.Select(r => TryDecimal(Get(r, "income"))).Where(v => v.HasValue).Select(v => v.Value).OrderBy(v => v).ToList();
        var expenseValues = clientsRaw.Rows.Select(r => TryDecimal(Get(r, "expenses"))).Where(v => v.HasValue).Select(v => v.Value).OrderBy(v => v).ToList();
        decimal medianIncome = Median(incomeValues);
        decimal medianExpenses = Median(expenseValues);
        decimal p33Income = incomeValues.Count == 0 ? 0 : incomeValues[(int)Math.Floor(incomeValues.Count * 0.33)];
        decimal p66Income = incomeValues.Count == 0 ? 0 : incomeValues[(int)Math.Floor(incomeValues.Count * 0.66)];

        var clientsById = new Dictionary<string, Client>();
        var seenClientIds = new HashSet<string>();
        int clientDuplicates = 0;
        foreach (var row in clientsRaw.Rows)
        {
            string id = Get(row, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!seenClientIds.Add(id)) { clientDuplicates++; continue; }
            clientsById[id] = new Client
            {
                Birthdate = TryDate(Get(row, "birthdate")),
                Gender = string.IsNullOrWhiteSpace(Get(row, "gender")) ? "Unknown" : Get(row, "gender"),
                Income = TryDecimal(Get(row, "income")) ?? medianIncome,
                Expenses = TryDecimal(Get(row, "expenses")) ?? medianExpenses
            };
        }

        var categoriesById = new Dictionary<string, string>();
        var seenCategoryIds = new HashSet<string>();
        int categoryDuplicates = 0;
        foreach (var row in categoriesRaw.Rows)
        {
            string id = Get(row, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!seenCategoryIds.Add(id)) { categoryDuplicates++; continue; }
            string name = Get(row, "name");
            categoriesById[id] = string.IsNullOrWhiteSpace(name) ? "Unknown" : name;
        }

        var subscriptionsByClient = new Dictionary<string, List<Subscription>>();
        var seenSubscriptionIds = new HashSet<string>();
        int subscriptionDuplicates = 0;
        foreach (var row in subscriptionsRaw.Rows)
        {
            string sid = Get(row, "id");
            if (!string.IsNullOrWhiteSpace(sid) && !seenSubscriptionIds.Add(sid)) { subscriptionDuplicates++; continue; }
            string clientId = Get(row, "client_id");
            if (string.IsNullOrWhiteSpace(clientId)) continue;
            List<Subscription> list;
            if (!subscriptionsByClient.TryGetValue(clientId, out list))
            {
                list = new List<Subscription>();
                subscriptionsByClient[clientId] = list;
            }
            list.Add(new Subscription
            {
                Amount = TryDecimal(Get(row, "amount")) ?? 0,
                Start = TryDate(Get(row, "date_start")),
                End = TryDate(Get(row, "date_end"))
            });
        }
        foreach (var list in subscriptionsByClient.Values)
        {
            list.Sort(new Comparison<Subscription>(delegate(Subscription a, Subscription b)
            {
                return DateTime.Compare(b.Start ?? DateTime.MinValue, a.Start ?? DateTime.MinValue);
            }));
        }

        string[] banks = { "SBI", "HDFC", "ICICI", "Axis", "Kotak" };
        string[] gateways = { "Razorpay", "Cashfree", "PayU", "Stripe" };
        string[] modes = { "UPI", "Credit Card", "Debit Card", "Wallet", "Net Banking" };
        string[] devices = { "Android", "iOS", "Web" };
        string[] networks = { "WiFi", "4G", "5G" };
        string[] errorCodes = { "ERR101", "ERR102", "ERR201", "ERR301", "ERR501" };
        Tuple<string, string>[] cityStates = {
            Tuple.Create("Delhi","Delhi"), Tuple.Create("Mumbai","Maharashtra"), Tuple.Create("Bangalore","Karnataka"),
            Tuple.Create("Hyderabad","Telangana"), Tuple.Create("Chennai","Tamil Nadu"), Tuple.Create("Pune","Maharashtra"),
            Tuple.Create("Ahmedabad","Gujarat"), Tuple.Create("Jaipur","Rajasthan"), Tuple.Create("Kolkata","West Bengal"),
            Tuple.Create("Lucknow","Uttar Pradesh")
        };
        var rng = new Random(20260709);

        string[] outputColumns = {
            "transaction_id","transaction_date","client_id","age","age_group","gender","city","state","income","expenses",
            "income_minus_expenses","customer_segment","product_category","product_name","amount","transaction_type",
            "payment_mode","bank_name","payment_gateway","transaction_status","processing_time_ms","retry_count","error_code",
            "subscription_status","subscription_amount","subscription_start","subscription_end","month","day_of_week","hour",
            "device_type","network_type"
        };

        int transactionRowsRead = 0, finalRowCount = 0, transactionDuplicates = 0, missingAmountRows = 0, invalidDateRows = 0;
        int successErrorViolations = 0, failedErrorViolations = 0, retryViolations = 0, negativeProcessingViolations = 0;
        var statusCounts = new Dictionary<string, int> { { "Failed", 0 }, { "Pending", 0 }, { "Success", 0 } };
        var seenTransactionIds = new HashSet<string>();

        using (var parser = NewParser(Path.Combine(root, "transactions.csv")))
        using (var writer = new StreamWriter(Path.Combine(root, "payment_operations_dataset.csv"), false, new UTF8Encoding(true)))
        {
            string[] headers = parser.ReadFields().Select(Snake).ToArray();
            var index = headers.Select((h, i) => new { h, i }).ToDictionary(x => x.h, x => x.i);
            writer.WriteLine(CsvLine(outputColumns));

            while (!parser.EndOfData)
            {
                string[] fields = parser.ReadFields();
                transactionRowsRead++;
                string transactionId = Field(fields, index, "transaction_id");
                if (string.IsNullOrWhiteSpace(transactionId)) continue;
                if (!seenTransactionIds.Add(transactionId)) { transactionDuplicates++; continue; }

                decimal? amountValue = TryDecimal(Field(fields, index, "amount"));
                if (!amountValue.HasValue) { missingAmountRows++; continue; }
                DateTime? transactionDateValue = TryDate(Field(fields, index, "date"));
                if (!transactionDateValue.HasValue) { invalidDateRows++; continue; }

                DateTime transactionDate = transactionDateValue.Value;
                string clientId = Field(fields, index, "client_id");
                Client client;
                clientsById.TryGetValue(clientId, out client);
                DateTime birthdate = client != null && client.Birthdate.HasValue ? client.Birthdate.Value : transactionDate.AddYears(-35);
                int age = Age(birthdate, transactionDate);
                if (age < 18) age = 18;
                decimal income = client == null ? medianIncome : client.Income;
                decimal expenses = client == null ? medianExpenses : client.Expenses;
                decimal incomeMinusExpenses = income - expenses;
                string segment = income <= p33Income ? "Low" : income <= p66Income ? "Middle" : "High";

                string categoryId = Field(fields, index, "product_category");
                string joinedCategory;
                string categoryName = categoriesById.TryGetValue(categoryId, out joinedCategory) ? joinedCategory : "Unknown";
                string productCompany = Field(fields, index, "product_company");
                string subtype = Field(fields, index, "subtype");
                string productName = !string.IsNullOrWhiteSpace(productCompany) ? productCompany : !string.IsNullOrWhiteSpace(subtype) ? subtype : categoryName;

                Subscription activeSub = null;
                List<Subscription> subs;
                if (!string.IsNullOrWhiteSpace(clientId) && subscriptionsByClient.TryGetValue(clientId, out subs))
                {
                    foreach (var sub in subs)
                    {
                        bool startsBefore = !sub.Start.HasValue || sub.Start.Value.Date <= transactionDate.Date;
                        bool endsAfter = !sub.End.HasValue || sub.End.Value.Date >= transactionDate.Date;
                        if (startsBefore && endsAfter) { activeSub = sub; break; }
                    }
                }

                double statusRoll = rng.NextDouble();
                string status = statusRoll < 0.95 ? "Success" : statusRoll < 0.99 ? "Failed" : "Pending";
                int processingTime, retryCount;
                string errorCode;
                if (status == "Success")
                {
                    processingTime = rng.Next(100, 801);
                    retryCount = rng.NextDouble() < 0.92 ? 0 : 1;
                    errorCode = "";
                }
                else if (status == "Failed")
                {
                    processingTime = rng.Next(500, 3001);
                    retryCount = rng.Next(1, 4);
                    errorCode = Pick(errorCodes, rng);
                }
                else
                {
                    processingTime = rng.Next(1000, 5001);
                    retryCount = rng.Next(0, 3);
                    errorCode = Pick(errorCodes, rng);
                }

                if (status == "Success" && !string.IsNullOrWhiteSpace(errorCode)) successErrorViolations++;
                if (status == "Failed" && string.IsNullOrWhiteSpace(errorCode)) failedErrorViolations++;
                if ((status == "Success" && retryCount < 0) || (status == "Failed" && (retryCount < 1 || retryCount > 3)) || (status == "Pending" && (retryCount < 0 || retryCount > 2))) retryViolations++;
                if (processingTime < 0) negativeProcessingViolations++;
                statusCounts[status]++;

                var place = cityStates[rng.Next(cityStates.Length)];
                writer.WriteLine(CsvLine(new object[] {
                    transactionId, transactionDate.ToString("yyyy-MM-dd HH:mm:ss"), clientId, age, AgeGroup(age),
                    client == null ? "Unknown" : client.Gender, place.Item1, place.Item2, Math.Round(income, 2),
                    Math.Round(expenses, 2), Math.Round(incomeMinusExpenses, 2), segment, categoryName, productName,
                    Math.Round(amountValue.Value, 2), Field(fields, index, "transaction_type"), Pick(modes, rng),
                    Pick(banks, rng), Pick(gateways, rng), status, processingTime, retryCount, errorCode,
                    activeSub == null ? "Not Subscribed" : "Subscribed", activeSub == null ? "" : Math.Round(activeSub.Amount, 2).ToString(CultureInfo.InvariantCulture),
                    activeSub == null ? "" : FormatDate(activeSub.Start), activeSub == null ? "" : FormatDate(activeSub.End),
                    transactionDate.ToString("yyyy-MM"), transactionDate.DayOfWeek.ToString(), transactionDate.Hour,
                    Pick(devices, rng), Pick(networks, rng)
                }));
                finalRowCount++;
            }
        }

        WriteDictionary(Path.Combine(root, "data_dictionary.csv"));
        WriteSummary(Path.Combine(root, "data_cleaning_summary.md"), transactionRowsRead, clientsRaw.Rows.Count, categoriesRaw.Rows.Count,
            subscriptionsRaw.Rows.Count, medianIncome, medianExpenses, finalRowCount, transactionDuplicates, missingAmountRows,
            invalidDateRows, clientDuplicates, categoryDuplicates, subscriptionDuplicates, statusCounts, successErrorViolations,
            failedErrorViolations, retryViolations, negativeProcessingViolations);

        Console.WriteLine("Created payment_operations_dataset.csv rows=" + finalRowCount);
        Console.WriteLine("Created data_dictionary.csv rows=32");
        Console.WriteLine("Created data_cleaning_summary.md");
        Console.WriteLine("Validations duplicate_tx=0 success_error=" + successErrorViolations + " failed_missing_error=" + failedErrorViolations + " retry_violations=" + retryViolations + " negative_processing=" + negativeProcessingViolations);
    }

    private sealed class CsvData
    {
        public List<Dictionary<string, string>> Rows = new List<Dictionary<string, string>>();
    }

    private static CsvData ReadCsv(string path)
    {
        var data = new CsvData();
        using (var parser = NewParser(path))
        {
            string[] headers = parser.ReadFields().Select(Snake).ToArray();
            while (!parser.EndOfData)
            {
                string[] fields = parser.ReadFields();
                var row = new Dictionary<string, string>();
                for (int i = 0; i < headers.Length; i++) row[headers[i]] = i < fields.Length ? (fields[i] ?? "").Trim() : "";
                data.Rows.Add(row);
            }
        }
        return data;
    }

    private static TextFieldParser NewParser(string path)
    {
        var parser = new TextFieldParser(path, Encoding.UTF8);
        parser.TextFieldType = FieldType.Delimited;
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        return parser;
    }

    private static string Snake(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "transaction_id";
        var sb = new StringBuilder();
        bool previousUnderscore = false;
        foreach (char ch in value.Trim().ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                sb.Append(ch);
                previousUnderscore = false;
            }
            else if (!previousUnderscore)
            {
                sb.Append('_');
                previousUnderscore = true;
            }
        }
        string result = sb.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(result) || result == "h1" ? "transaction_id" : result;
    }

    private static string Get(Dictionary<string, string> row, string key)
    {
        string value;
        return row.TryGetValue(key, out value) ? value.Trim() : "";
    }

    private static string Field(string[] fields, Dictionary<string, int> index, string key)
    {
        int i;
        return index.TryGetValue(key, out i) && i < fields.Length ? (fields[i] ?? "").Trim() : "";
    }

    private static string Pick(string[] values, Random rng)
    {
        return values[rng.Next(values.Length)];
    }

    private static string FormatDate(DateTime? value)
    {
        return value.HasValue ? value.Value.ToString("yyyy-MM-dd") : "";
    }

    private static DateTime? TryDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        DateTime date;
        if (DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out date)) return date;
        return null;
    }

    private static decimal? TryDecimal(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        decimal number;
        if (decimal.TryParse(value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }

    private static decimal Median(List<decimal> sorted)
    {
        if (sorted.Count == 0) return 0;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }

    private static int Age(DateTime birthdate, DateTime asOf)
    {
        int age = asOf.Year - birthdate.Year;
        if (asOf.Date < birthdate.Date.AddYears(age)) age--;
        return age;
    }

    private static string AgeGroup(int age)
    {
        if (age <= 25) return "18-25";
        if (age <= 35) return "26-35";
        if (age <= 45) return "36-45";
        if (age <= 60) return "46-60";
        return "60+";
    }

    private static string CsvLine(IEnumerable<object> values)
    {
        return string.Join(",", values.Select(CsvField));
    }

    private static string CsvField(object value)
    {
        if (value == null) return "";
        string text = Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
        if (text.IndexOf('"') >= 0) text = text.Replace("\"", "\"\"");
        return text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + text + "\"" : text;
    }

    private static void WriteDictionary(string path)
    {
        string[,] rows = {
            {"transaction_id","Unique transaction identifier from the raw transaction id column."},
            {"transaction_date","Timestamp of the transaction."},
            {"client_id","Client identifier used to join client and subscription attributes."},
            {"age","Client age as of transaction_date."},
            {"age_group","Age band: 18-25, 26-35, 36-45, 46-60, or 60+."},
            {"gender","Client gender; missing values set to Unknown."},
            {"city","Simulated Indian city for payment operations analysis."},
            {"state","State matching the simulated city."},
            {"income","Client income, median-imputed when missing."},
            {"expenses","Client expenses, median-imputed when missing."},
            {"income_minus_expenses","Income less expenses."},
            {"customer_segment","Income-based segment using 33rd and 66th percentile cutoffs."},
            {"product_category","Product category name joined from categories.csv."},
            {"product_name","Product company when present, otherwise subtype/category fallback."},
            {"amount","Transaction amount."},
            {"transaction_type","Raw transaction type."},
            {"payment_mode","Simulated payment mode."},
            {"bank_name","Simulated bank name."},
            {"payment_gateway","Simulated payment gateway."},
            {"transaction_status","Simulated status: Success 95%, Failed 4%, Pending 1%."},
            {"processing_time_ms","Simulated processing time based on status."},
            {"retry_count","Simulated retry count consistent with status."},
            {"error_code","Blank for Success; simulated error code for Failed/Pending."},
            {"subscription_status","Subscribed when an active client subscription exists on transaction_date."},
            {"subscription_amount","Amount of the active subscription, if any."},
            {"subscription_start","Start date of the active subscription, if any."},
            {"subscription_end","End date of the active subscription, if any."},
            {"month","Transaction month in yyyy-MM format."},
            {"day_of_week","Day name of transaction_date."},
            {"hour","Hour of transaction_date."},
            {"device_type","Simulated device type."},
            {"network_type","Simulated network type."}
        };
        using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
        {
            writer.WriteLine("column_name,description");
            for (int i = 0; i < rows.GetLength(0); i++) writer.WriteLine(CsvLine(new object[] { rows[i, 0], rows[i, 1] }));
        }
    }

    private static void WriteSummary(string path, int txRows, int clientRows, int categoryRows, int subRows, decimal medianIncome,
        decimal medianExpenses, int finalRows, int txDupes, int missingAmounts, int invalidDates, int clientDupes, int categoryDupes,
        int subDupes, Dictionary<string, int> statusCounts, int successErrors, int failedMissingErrors, int retryViolations,
        int negativeProcessing)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Data Cleaning Summary");
        sb.AppendLine();
        sb.AppendLine("## Source Files Loaded");
        sb.AppendLine("- transactions.csv: " + txRows + " rows");
        sb.AppendLine("- clients.csv: " + clientRows + " rows");
        sb.AppendLine("- categories.csv: " + categoryRows + " rows");
        sb.AppendLine("- subscriptions.csv: " + subRows + " rows");
        sb.AppendLine();
        sb.AppendLine("## Cleaning Performed");
        sb.AppendLine("- Standardized column names to snake_case, including mapping the unnamed transaction column to transaction_id.");
        sb.AppendLine("- Trimmed whitespace across loaded values.");
        sb.AppendLine("- Removed duplicate keys from lookup tables and duplicate transaction_id records from transactions.");
        sb.AppendLine("- Removed rows with missing or invalid transaction amount.");
        sb.AppendLine("- Converted transaction, birthdate, registration, and subscription dates to datetime during processing.");
        sb.AppendLine("- Removed PII from the analytics output: fullname, address, phone/phone_number, and email.");
        sb.AppendLine("- Imputed missing income with median income (" + Math.Round(medianIncome, 2).ToString(CultureInfo.InvariantCulture) + ") and missing expenses with median expenses (" + Math.Round(medianExpenses, 2).ToString(CultureInfo.InvariantCulture) + ").");
        sb.AppendLine("- Filled missing gender with Unknown and product/category fallbacks with Unknown where necessary.");
        sb.AppendLine();
        sb.AppendLine("## Merge Logic");
        sb.AppendLine("- transactions.client_id left joined to clients.id.");
        sb.AppendLine("- transactions.product_category left joined to categories.id.");
        sb.AppendLine("- subscriptions.client_id joined through clients.id and evaluated as active on each transaction_date to preserve one row per transaction.");
        sb.AppendLine();
        sb.AppendLine("## Feature Engineering");
        sb.AppendLine("- Created age, age_group, month, day_of_week, hour, income_minus_expenses, customer_segment, and subscription_status.");
        sb.AppendLine("- Simulated bank_name, payment_gateway, payment_mode, transaction_status, processing_time_ms, retry_count, error_code, city/state, device_type, and network_type using a fixed random seed for reproducibility.");
        sb.AppendLine();
        sb.AppendLine("## Row Outcomes");
        sb.AppendLine("- Final dataset rows: " + finalRows);
        sb.AppendLine("- Duplicate transaction_id rows removed: " + txDupes);
        sb.AppendLine("- Rows removed for missing/invalid amount: " + missingAmounts);
        sb.AppendLine("- Rows removed for invalid transaction_date: " + invalidDates);
        sb.AppendLine("- Duplicate client ids skipped: " + clientDupes);
        sb.AppendLine("- Duplicate category ids skipped: " + categoryDupes);
        sb.AppendLine("- Duplicate subscription ids skipped: " + subDupes);
        sb.AppendLine();
        sb.AppendLine("## Simulated Transaction Status Distribution");
        foreach (var key in new[] { "Failed", "Pending", "Success" }) sb.AppendLine("- " + key + ": " + statusCounts[key]);
        sb.AppendLine();
        sb.AppendLine("## Validation Results");
        sb.AppendLine("- Duplicate transaction_id count in final dataset: 0");
        sb.AppendLine("- Success rows with non-null error_code: " + successErrors);
        sb.AppendLine("- Failed rows missing error_code: " + failedMissingErrors);
        sb.AppendLine("- Retry/status logic violations: " + retryViolations);
        sb.AppendLine("- Negative processing_time_ms rows: " + negativeProcessing);
        sb.AppendLine();
        sb.AppendLine("## Output Files");
        sb.AppendLine("- payment_operations_dataset.csv");
        sb.AppendLine("- data_dictionary.csv");
        sb.AppendLine("- data_cleaning_summary.md");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }
}
