using System;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Linq;

namespace XBeeTest
{
    public partial class MainWindow : Window
    {
        private SerialPort serial;
        private string buffer = "";

        // より柔軟な正規表現パターン（末尾文字の変化に対応）
        private readonly Regex dataPattern = new Regex(@"([+-])(\d{4,5})", RegexOptions.Compiled);

        private int lastProcessedPosition = 0;
        private DateTime lastDataTime = DateTime.MinValue;
        private string lastDisplayValue = "";

        public MainWindow()
        {
            InitializeComponent();
            PopulateSerialPorts();
        }

        private void PopulateSerialPorts()
        {
            PortComboBox.Items.Clear();
            string[] ports = SerialPort.GetPortNames();

            foreach (string port in ports.OrderBy(p => p))
            {
                PortComboBox.Items.Add(port);
            }

            // デフォルトでCOM4を選択（存在する場合）
            if (PortComboBox.Items.Contains("COM5"))
            {
                PortComboBox.SelectedItem = "COM5";
            }
            else if (PortComboBox.Items.Count > 0)
            {
                PortComboBox.SelectedIndex = 0;
            }
        }

        private void RefreshPortsButton_Click(object sender, RoutedEventArgs e)
        {
            PopulateSerialPorts();
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (serial?.IsOpen == true)
            {
                DisconnectSerial();
            }
            else
            {
                ConnectSerial();
            }
        }

        private void ConnectSerial()
        {
            try
            {
                if (PortComboBox.SelectedItem == null)
                {
                    UpdateStatus("ポートを選択してください", Brushes.Red);
                    return;
                }

                string selectedPort = PortComboBox.SelectedItem.ToString();
                serial = new SerialPort(selectedPort, 9600, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 100,
                    WriteTimeout = 100,
                    ReceivedBytesThreshold = 1
                };

                serial.DataReceived += Serial_DataReceived;
                serial.Open();

                // バッファとカウンターをリセット
                buffer = "";
                lastProcessedPosition = 0;
                lastDataTime = DateTime.Now;

                UpdateStatus($"{selectedPort} に接続済み", Brushes.Green);
                ConnectButton.Content = "切断";
                PortComboBox.IsEnabled = false;
                RefreshPortsButton.IsEnabled = false;

                RealTimeDisplay.Text = "データ待機中...";
                UpdateDebugInfo("シリアル接続が確立されました。データ待機中...");
            }
            catch (Exception ex)
            {
                UpdateStatus($"接続エラー: {ex.Message}", Brushes.Red);
                UpdateDebugInfo($"接続エラー詳細: {ex}");
            }
        }

        private void DisconnectSerial()
        {
            try
            {
                serial?.Close();
                serial?.Dispose();
                serial = null;

                UpdateStatus("未接続", Brushes.Gray);
                ConnectButton.Content = "接続";
                PortComboBox.IsEnabled = true;
                RefreshPortsButton.IsEnabled = true;
                RealTimeDisplay.Text = "000.0m";
                RawDataDisplay.Text = "生データ: なし";
                UpdateDebugInfo("シリアル接続を切断しました。");
            }
            catch (Exception ex)
            {
                UpdateStatus($"切断エラー: {ex.Message}", Brushes.Red);
            }
        }

        private void UpdateStatus(string message, Brush color)
        {
            Dispatcher.Invoke(() =>
            {
                StatusDisplay.Text = message;
                StatusDisplay.Foreground = color;
            });
        }

        private void UpdateDebugInfo(string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                string newLine = $"[{timestamp}] {message}\n";
                DebugDisplay.Text += newLine;

                // デバッグ情報が長くなりすぎないように制限
                var lines = DebugDisplay.Text.Split('\n');
                if (lines.Length > 30)
                {
                    DebugDisplay.Text = string.Join("\n", lines.Skip(lines.Length - 25));
                }
            }), DispatcherPriority.Background);
        }

        private void Serial_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                // 利用可能なバイト数を取得
                int bytesToRead = serial.BytesToRead;
                if (bytesToRead == 0) return;

                // 生バイトデータを読み取り
                byte[] rawBytes = new byte[bytesToRead];
                serial.Read(rawBytes, 0, bytesToRead);

                // バイトデータをクリーンな文字列に変換
                string receivedData = ConvertBytesToCleanString(rawBytes);

                if (string.IsNullOrEmpty(receivedData)) return;

                buffer += receivedData;
                lastDataTime = DateTime.Now;

                // 改善された処理方法でバッファを解析
                ProcessBufferImproved();

                // バッファ管理（短く保つ）
                if (buffer.Length > 500)
                {
                    // 最後の有効なデータパターンを探して、その後ろからバッファを保持
                    var matches = dataPattern.Matches(buffer);
                    if (matches.Count > 0)
                    {
                        var lastMatch = matches[matches.Count - 1];
                        int keepFromIndex = Math.Max(0, lastMatch.Index - 50);
                        buffer = buffer.Substring(keepFromIndex);
                        lastProcessedPosition = Math.Max(0, lastProcessedPosition - keepFromIndex);
                    }
                    else
                    {
                        buffer = buffer.Substring(Math.Max(0, buffer.Length - 200));
                        lastProcessedPosition = 0;
                    }
                }

                // デバッグ情報更新（パフォーマンス考慮して間引き）
                if (buffer.Length % 50 == 0)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        string hexData = BitConverter.ToString(rawBytes.Take(Math.Min(10, rawBytes.Length)).ToArray());
                        RawDataDisplay.Text = $"バッファ: {buffer.Length} | HEX: {hexData} | 最新: '{receivedData.Substring(Math.Max(0, receivedData.Length - 15))}'";
                    }), DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateStatus($"データ受信エラー: {ex.Message}", Brushes.Red);
                    UpdateDebugInfo($"受信エラー: {ex.Message}");
                }), DispatcherPriority.Background);
            }
        }

        // バイトデータをクリーンな文字列に変換
        private string ConvertBytesToCleanString(byte[] bytes)
        {
            var result = new System.Text.StringBuilder();

            foreach (byte b in bytes)
            {
                // 有効な文字のみを保持
                if ((b >= 48 && b <= 57) ||  // 数字 0-9
                    b == 43 || b == 45 ||     // + と -
                    b == 42 ||                // *
                    (b >= 32 && b <= 126) ||  // 印刷可能ASCII文字
                    b == 13 || b == 10)       // CR, LF
                {
                    result.Append((char)b);
                }
                // 不要な制御文字や非ASCII文字は無視
            }

            return result.ToString();
        }

        // 改善されたバッファ処理
        private void ProcessBufferImproved()
        {
            // 最後に見つかった有効なパターンを取得
            var matches = dataPattern.Matches(buffer);

            if (matches.Count > 0)
            {
                // 最新の（最後の）マッチを使用
                var lastMatch = matches[matches.Count - 1];

                // 既に処理済みの位置より後のマッチのみを対象にする
                if (lastMatch.Index + lastMatch.Length > lastProcessedPosition)
                {
                    lastProcessedPosition = lastMatch.Index + lastMatch.Length;

                    string sign = lastMatch.Groups[1].Value;
                    string digits = lastMatch.Groups[2].Value;

                    // 数値フォーマット
                    string formattedValue = FormatNumberRobust(sign, digits);

                    // 値が変更された場合のみUI更新
                    if (formattedValue != lastDisplayValue)
                    {
                        lastDisplayValue = formattedValue;

                        // UIを高速更新
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            RealTimeDisplay.Text = formattedValue;
                            RealTimeDisplay.Foreground = sign == "+" ? Brushes.LimeGreen : Brushes.Red;
                        }), DispatcherPriority.Render);

                        // デバッグ情報
                        UpdateDebugInfo($"抽出: '{lastMatch.Value}' -> {formattedValue}");
                    }
                }
            }
        }

        // 堅牢な数値フォーマット処理
        private string FormatNumberRobust(string sign, string digits)
        {
            try
            {
                // 桁数に応じて処理
                if (digits.Length >= 4)
                {
                    // 4桁以上の場合: xxx.x 形式に変換
                    string integerPart = digits.Substring(0, digits.Length - 1);
                    string decimalPart = digits.Substring(digits.Length - 1);

                    // 先頭の0を除去（ただし、全部0の場合は"0"を残す）
                    integerPart = integerPart.TrimStart('0');
                    if (string.IsNullOrEmpty(integerPart))
                        integerPart = "0";

                    return $"{sign}{integerPart}.{decimalPart}m";
                }
                else
                {
                    // 4桁未満の場合: そのまま.0を付加
                    string cleanDigits = digits.TrimStart('0');
                    if (string.IsNullOrEmpty(cleanDigits))
                        cleanDigits = "0";

                    return $"{sign}{cleanDigits}.0m";
                }
            }
            catch (Exception ex)
            {
                // エラーの場合は安全な形式で返す
                UpdateDebugInfo($"フォーマットエラー: {ex.Message} (sign:{sign}, digits:{digits})");
                return $"{sign}{digits}m";
            }
        }

        // 古いCleanReceivedDataメソッドは削除（ConvertBytesToCleanStringで代替）

        protected override void OnClosed(EventArgs e)
        {
            DisconnectSerial();
            base.OnClosed(e);
        }
    }
}