using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

using Dapper;
using Flurl;
using Flurl.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Represents a single game with an AppToken and a PromoID, as loaded from a JSON file.
/// </summary>
public class Game
{
    public string name { get; set; }
    public string appToken { get; set; }
    public string promoId { get; set; }
    public string platform { get; set; }
    public int eventsDelay { get; set; }
}

/// <summary>
/// Main form for generating and handling game keys.
/// Demonstrates usage of asynchronous calls, concurrency, and basic UI updates.
/// </summary>
public partial class FormMain : Form
{
    private Game[] _games = Array.Empty<Game>();
    private static int _progressTime = 0;

    // UI references for easier static usage
    private static ToolStripLabel _labelKeys;
    private static ToolStripLabel _labelRequest;
    private static NumericUpDown _delayNumericInput;
    private static ListBox _listBoxKeys;
    private static ProgressBar _progressBarMain;
    private static RichTextBox _richTextBoxLogs;

    /// <summary>
    /// Instantiates and initializes the main form.
    /// Loads the game definitions from 'games.json' and sets up a local SQLite db for keys.
    /// </summary>
    public FormMain()
    {
        InitializeComponent();
        LoadGamesFromJson();
        SetupDatabase();
    }

    /// <summary>
    /// Load the game definitions from a local 'games.json' file, if present.
    /// </summary>
    private void LoadGamesFromJson()
    {
        string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "games.json");
        if (!File.Exists(filePath))
        {
            Log("games.json not found. Please ensure it is placed next to the application.", Color.Red);
            _games = Array.Empty<Game>();
            return;
        }

        string jsonContent = File.ReadAllText(filePath);
        try
        {
            _games = JsonConvert.DeserializeObject<Game[]>(jsonContent) ?? Array.Empty<Game>();
        }
        catch (Exception ex)
        {
            Log("Error parsing games.json: " + ex.Message, Color.Red);
            _games = Array.Empty<Game>();
        }
    }

    /// <summary>
    /// Creates a local SQLite database if it does not exist, containing a 'keys' table.
    /// </summary>
    private void SetupDatabase()
    {
        try
        {
            using (var connection = new SQLiteConnection("Data Source=keys.db;Version=3;"))
            {
                connection.Open();

                string createTableQuery = @"
                    CREATE TABLE IF NOT EXISTS keys (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        key TEXT UNIQUE NOT NULL,
                        platform TEXT NOT NULL
                    );";
                connection.Execute(createTableQuery);
                connection.Close();
            }
        }
        catch (Exception ex)
        {
            Log("Error setting up local database: " + ex.Message, Color.Red);
        }
    }

    /// <summary>
    /// Initialize form, fill comboBox, start timer, link references, etc.
    /// </summary>
    private void FormMain_Load(object sender, EventArgs e)
    {
        RefreshUIReferences();
        timer1.Start();

        comboBoxGames.Items.Clear();
        if (_games?.Length > 0)
        {
            foreach (var gm in _games)
            {
                comboBoxGames.Items.Add(gm.name);
            }
        }
        else
        {
            Log("No games loaded. Ensure games.json is valid.", Color.Orange);
        }
    }

    /// <summary>
    /// Refresh references to UI elements for static usage.
    /// This approach is not recommended in production code but used here for convenience.
    /// </summary>
    private void RefreshUIReferences()
    {
        _labelKeys = labelKeys;
        _labelRequest = labelRequest;
        _delayNumericInput = numericUpDownMinimumDelay;
        _listBoxKeys = listBoxKeys;
        _progressBarMain = progressBarMain;
        _richTextBoxLogs = richTextBoxLogs;
    }

    private async void buttonGenerate_Click(object sender, EventArgs e)
    {
        if (comboBoxGames.SelectedIndex < 0)
        {
            Log("Please select a game first.", Color.Orange);
            return;
        }

        var selectedGame = _games.FirstOrDefault(x => x.name == comboBoxGames.Text);
        if (selectedGame == null)
        {
            Log("Could not find the selected game in the loaded list.", Color.Orange);
            return;
        }

        buttonGenerate.Enabled = false;

        int processCount = (int)numericUpDownProccess.Value;
        labelProccess.Text = $"Processes: {processCount}";

        var tasks = new Task[processCount];
        for (int i = 0; i < processCount; i++)
        {
            if (checkBoxProccessRandomDelay.Checked)
            {
                // random offset up to 5s
                await Task.Delay(new Random().Next(0, 5000));
            }
            tasks[i] = ProcessFlowAsync(selectedGame, (int)numericUpDownCount.Value, "Proc_" + (i + 1));
        }

        await Task.WhenAll(tasks);

        Log("All processes finished", Color.LimeGreen);
        buttonGenerate.Enabled = true;
    }

    #region Network Logic

    /// <summary>
    /// Logs in with the provided token, returning the 'clientToken'.
    /// </summary>
    /// <param name="token">appToken from the game object</param>
    /// <returns>clientToken string</returns>
    public static async Task<string> Login(string token)
    {
        const string url = "https://api.gamepromo.io/promo/login-client";

        var body = new
        {
            appToken = token,
            clientId = GenerateClientId(),
            clientOrigin = "deviceid"
        };

        string jsonBody = JsonConvert.SerializeObject(body);

        using (var client = new HttpClient())
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };

            var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            JObject jsonObject = JObject.Parse(responseBody);
            return (string)jsonObject["clientToken"];
        }
    }

    /// <summary>
    /// Sends a 'register-event' request, returning an int code for the result.
    /// 200 => success (hasCode = true)
    /// 1 => success but hasCode=false
    /// 2 => unauthorized
    /// 3 => too many register attempts
    /// 400 => other errors
    /// </summary>
    public static async Task<int> RegisterEventAsync(string clientToken, string promoId)
    {
        const string url = "https://api.gamepromo.io/promo/register-event";

        var body = new
        {
            promoId = promoId,
            eventId = Guid.NewGuid().ToString(),
            eventOrigin = "undefined"
        };

        try
        {
            string response = await url
                .WithOAuthBearerToken(clientToken)
                .AllowHttpStatus("4xx")
                .PostJsonAsync(body)
                .ReceiveString();

            JObject jsonObject = JObject.Parse(response);

            if (jsonObject["hasCode"] != null)
            {
                bool hasCode = jsonObject.Value<bool>("hasCode");
                return hasCode ? 200 : 1;
            }
            else if (jsonObject["error_code"] != null)
            {
                string errorCode = jsonObject["error_code"].ToString().ToLowerInvariant();
                switch (errorCode)
                {
                    case "unauthorized":
                        return 2;
                    case "toomanyregister":
                        return 3;
                }
            }
            return 400;
        }
        catch
        {
            // we might rethrow or return some code
            throw;
        }
    }

    /// <summary>
    /// Generates the actual promo code using the 'create-code' endpoint.
    /// Returns the promo code if successful, otherwise null.
    /// </summary>
    public static async Task<string> GenerateKey(string clientToken, string promoId)
    {
        const string url = "https://api.gamepromo.io/promo/create-code";

        var body = new
        {
            promoId
        };

        try
        {
            var response = await url
                .WithOAuthBearerToken(clientToken)
                .AllowHttpStatus("4xx")
                .PostJsonAsync(body)
                .ReceiveString();

            JObject jsonObject = JObject.Parse(response);
            return (string)jsonObject["promoCode"];
        }
        catch
        {
            throw;
        }
    }

    /// <summary>
    /// For a given 'appToken' from the selectedGame and a desired number of keys, handle the entire flow:
    /// - login
    /// - repeated calls to register event
    /// - generate final code
    /// - store in local db
    /// - handle random delays, errors, etc.
    /// </summary>
    static private async Task ProcessFlowAsync(Game game, int totalKeys, string processName)
    {
        for (int i = 1; i <= totalKeys; i++)
        {
            Log($"Start generating key {i}", section: processName);

            string clientToken;
            try
            {
                clientToken = await Login(game.appToken);
                Log($"Client token: {clientToken}", section: processName);
            }
            catch
            {
                Log("Login failed", Color.Red, section: processName);
                // Retry same key
                i--;
                await Task.Delay(3000);
                continue;
            }

            int attempts = 1;
            int response = 0;
            int surplusDelay = 0;
            bool restartClient = false;
            int repeatedSuccessCount = 0;
            Random rand = new Random();

            Log("Sending requests ...", Color.Cyan, section: processName);

            // keep going until 'response=200' => hasCode = true
            while (response != 200)
            {
                int randomJitter = rand.Next(-2000, 2000);
                int finalDelay = game.eventsDelay + randomJitter + surplusDelay;
                if (finalDelay < _delayNumericInput.Value)
                {
                    finalDelay = (int)_delayNumericInput.Value + rand.Next(0, 1000);
                }

                if (_progressBarMain.Value == 0 || _progressBarMain.Value == 100)
                {
                    _progressBarMain.Value = 0;
                    _progressTime = finalDelay;
                }

                Log($"Request {attempts} (delay={finalDelay}ms)...", section: processName);
                _labelRequest.Text = $"Last request: {attempts}";
                attempts++;

                await Task.Delay(finalDelay);

                try
                {
                    response = await RegisterEventAsync(clientToken, game.promoId);
                }
                catch
                {
                    Log("HTTP error registering event", Color.Red, section: processName);
                    continue; 
                }

                switch (response)
                {
                    case 2:
                        repeatedSuccessCount = 0;
                        Log("Session unauthorized => Re-login needed", Color.Red, section: processName);
                        restartClient = true;
                        break;
                    case 3:
                        repeatedSuccessCount = 0;
                        Log("Requests are happening too fast => Increase delay", Color.Orange, section: processName);
                        surplusDelay += 2000;
                        break;
                    case 400:
                        repeatedSuccessCount = 0;
                        Log("Undefined error from server => continue attempts", Color.DarkOrange, section: processName);
                        break;
                    case 1:
                        repeatedSuccessCount++;
                        if (repeatedSuccessCount >= 3)
                        {
                            Log("Reducing surplus delay slightly", Color.Orange, section: processName);
                            surplusDelay -= 1000;
                            repeatedSuccessCount = 0;
                        }
                        break;
                    default:
                        break;
                }

                _progressBarMain.Value = 100;

                if (restartClient) break;
            }

            if (restartClient)
            {
                i--;
                continue;
            }

            // Now we get the final key
            string promoKey;
            try
            {
                promoKey = await GenerateKey(clientToken, game.promoId);
                if (string.IsNullOrWhiteSpace(promoKey))
                {
                    Log("Promo code is empty => re-try", Color.Red, section: processName);
                    i--;
                    continue;
                }
            }
            catch
            {
                Log("GenerateKey() error => re-try", Color.Red, section: processName);
                i--;
                continue;
            }

            int dbResult = AddKeyToDatabase(promoKey, game.platform);
            switch (dbResult)
            {
                case 0:
                    Log("Failed saving key in local DB", Color.Red, section: processName);
                    break;
                case 100:
                    Log("Key already exists in DB", Color.HotPink, section: processName);
                    break;
            }
            _listBoxKeys.Items.Add(promoKey);
            _listBoxKeys.TopIndex = _listBoxKeys.Items.Count - 1;
            _labelKeys.Text = $"Keys: {_listBoxKeys.Items.Count}";

            Log($"Key generated: {promoKey}", Color.LimeGreen, section: processName);
        }
        Log("Process complete", Color.Lime, section: processName);
    }

    #endregion

    #region Local DB

    /// <summary>
    /// Insert or check key existence in local SQLite DB. Returns:
    /// 200 => inserted
    /// 100 => key already exists
    /// 0 => error
    /// </summary>
    static private int AddKeyToDatabase(string key, string platform)
    {
        try
        {
            using (var connection = new SQLiteConnection("Data Source=keys.db;Version=3;"))
            {
                connection.Open();
                // Check existing
                const string checkKeySql = "SELECT COUNT(1) FROM keys WHERE key = @Key;";
                int keyExists = connection.QuerySingle<int>(checkKeySql, new { Key = key });
                if (keyExists > 0) return 100;

                // Insert
                const string insertSql = "INSERT INTO keys (key, platform) VALUES (@Key, @Platform);";
                int inserted = connection.Execute(insertSql, new { Key = key, Platform = platform });

                return inserted > 0 ? 200 : 0;
            }
        }
        catch (Exception ex)
        {
            Log("DB Error: " + ex.Message, Color.Red);
            return 0;
        }
    }

    #endregion

    #region Utility

    /// <summary>
    /// Called by timer1 to update progress bar if we have a target wait time.
    /// </summary>
    private void timer1_Tick(object sender, EventArgs e)
    {
        if (_progressTime <= 0) return;

        // Because Timer Interval is small, we do approximate
        int increment = 100 / (_progressTime / timer1.Interval);
        if (_progressBarMain.Value + increment < 100)
        {
            _progressBarMain.Value += increment;
        }
    }

    /// <summary>
    /// Generate a unique device ID to pass in the JSON body for the client. 
    /// Combines a timestamp and random digits for uniqueness.
    /// </summary>
    public static string GenerateClientId()
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Random r = new Random();
        string randomNumbers = string.Concat(Enumerable.Range(0, 19).Select(_ => r.Next(10)));
        return $"{timestamp}-{randomNumbers}";
    }

    /// <summary>
    /// Writes log messages in the RichTextBox with optional color and optional 'section' name.
    /// Also includes a timestamp.
    /// </summary>
    static private void Log(string text, Color color = default, string section = null)
    {
        DateTime now = DateTime.Now;
        string formattedTime = now.ToString("HH:mm:ss");
        Color originalColor = _richTextBoxLogs.SelectionColor;

        // Prepend time
        _richTextBoxLogs.AppendText($"[{formattedTime}]");

        // If we have a section name, color it using a stable color derived from the text
        if (!string.IsNullOrEmpty(section))
        {
            _richTextBoxLogs.AppendText("(");
            _richTextBoxLogs.SelectionColor = GetColorFromText(section);
            _richTextBoxLogs.AppendText(section);
            _richTextBoxLogs.SelectionColor = originalColor;
            _richTextBoxLogs.AppendText(")");
        }

        _richTextBoxLogs.AppendText(" ");
        _richTextBoxLogs.SelectionColor = color.IsEmpty ? Color.White : color;

        _richTextBoxLogs.AppendText($"{text}\n");
        _richTextBoxLogs.SelectionColor = originalColor;
        _richTextBoxLogs.ScrollToCaret();
    }

    /// <summary>
    /// Create a stable color for the text input by hashing with SHA256, extracting a hue from the result,
    /// then converting from HSL to RGB.
    /// </summary>
    public static Color GetColorFromText(string input)
    {
        using (var sha256 = SHA256.Create())
        {
            byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(input + "artariya"));
            int hue = BitConverter.ToUInt16(hash, 0) % 360;
            return HslToRgb(hue, 1.0, 0.5);
        }
    }

    /// <summary>
    /// Convert hue-saturation-lightness to an RGB color.
    /// </summary>
    public static Color HslToRgb(double h, double s, double l)
    {
        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
        double m = l - c / 2.0;

        (double r, double g, double b) = (0, 0, 0);

        if (h < 60)         (r, g, b) = (c, x, 0);
        else if (h < 120)   (r, g, b) = (x, c, 0);
        else if (h < 180)   (r, g, b) = (0, c, x);
        else if (h < 240)   (r, g, b) = (0, x, c);
        else if (h < 300)   (r, g, b) = (x, 0, c);
        else                (r, g, b) = (c, 0, x);

        int R = (int)((r + m) * 255);
        int G = (int)((g + m) * 255);
        int B = (int)((b + m) * 255);
        return Color.FromArgb(R, G, B);
    }

    #endregion

    #region UI Event Handlers

    /// <summary>
    /// Copies the currently selected key from the listBox to the clipboard, if any.
    /// </summary>
    private void buttonCopy_Click(object sender, EventArgs e)
    {
        if (listBoxKeys.SelectedItem != null)
        {
            string key = listBoxKeys.SelectedItem.ToString();
            Clipboard.SetText(key);
            Log($"Item copied to clipboard: {key}", Color.Yellow);
        }
        else
        {
            Log("No item selected to copy.", Color.Yellow);
        }
    }

    /// <summary>
    /// Copies all items from the listBox to the clipboard, each on its own line.
    /// </summary>
    private void buttonCopyAll_Click(object sender, EventArgs e)
    {
        var builder = new StringBuilder();
        foreach (var item in listBoxKeys.Items)
        {
            builder.AppendLine(item.ToString());
        }
        Clipboard.SetText(builder.ToString());
        Log("All keys copied to clipboard.", Color.Yellow);
    }

    /// <summary>
    /// When changing the game in the comboBox, update the numericUpDownMinimumDelay accordingly.
    /// </summary>
    private void comboBoxGames_SelectedIndexChanged(object sender, EventArgs e)
    {
        var selectedGame = _games.FirstOrDefault(x => x.name == comboBoxGames.Text);
        if (selectedGame == null) return;

        numericUpDownMinimumDelay.Value = selectedGame.eventsDelay;
    }

    #endregion
}

