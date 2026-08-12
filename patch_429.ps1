$f = "C:\code\FlickrSlideshow\FlickrSlideshow.XboxTest\MainWindow.xaml.cs"
$c = [System.IO.File]::ReadAllText($f)

# ── Fix StartButton reset label to match ─────────────────────────────────────
$c = $c.Replace(
	'lblGoodCount.Content = $"Good Count: {_successCount}";',
	'lblGoodCount.Content = "Good: 0";'
)

# ── Capture 429 details and show button ───────────────────────────────────────
# Find the 429 branch and replace it to capture headers+body before the existing logic
$old429First = "            if (status == HttpStatusCode.TooManyRequests)" + [char]13 + [char]10 +
			   "            {" + [char]13 + [char]10 +
			   "                if (!everGot429)"

$new429First = "            if (status == HttpStatusCode.TooManyRequests)" + [char]13 + [char]10 +
			   "            {" + [char]13 + [char]10 +
			   "                // Capture full 429 details" + [char]13 + [char]10 +
			   "                var sb429 = new System.Text.StringBuilder();" + [char]13 + [char]10 +
			   "                sb429.AppendLine(`$`"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}`");" + [char]13 + [char]10 +
			   "                sb429.AppendLine(`$`"URL: {url}`");" + [char]13 + [char]10 +
			   "                sb429.AppendLine();" + [char]13 + [char]10 +
			   "                sb429.AppendLine(`"=== Response Headers ===`");" + [char]13 + [char]10 +
			   "                foreach (var kv in resp.Headers)" + [char]13 + [char]10 +
			   "                    sb429.AppendLine(`$`"{kv.Key}: {string.Join(`", `", kv.Value)}`");" + [char]13 + [char]10 +
			   "                sb429.AppendLine();" + [char]13 + [char]10 +
			   "                sb429.AppendLine(`"=== Content Headers ===`");" + [char]13 + [char]10 +
			   "                foreach (var kv in resp.Content.Headers)" + [char]13 + [char]10 +
			   "                    sb429.AppendLine(`$`"{kv.Key}: {string.Join(`", `", kv.Value)}`");" + [char]13 + [char]10 +
			   "                sb429.AppendLine();" + [char]13 + [char]10 +
			   "                sb429.AppendLine(`"=== Body ===`");" + [char]13 + [char]10 +
			   "                var body429 = await resp.Content.ReadAsStringAsync(token);" + [char]13 + [char]10 +
			   "                sb429.Append(body429.Length > 4000 ? body429[..4000] + `"...`" : body429);" + [char]13 + [char]10 +
			   "                _last429Dump = sb429.ToString();" + [char]13 + [char]10 +
			   "                Dispatcher.Invoke(() => Show429Button.Visibility = Visibility.Visible);" + [char]13 + [char]10 +
			   [char]13 + [char]10 +
			   "                if (!everGot429)"

$found = $c.Contains($old429First)
Write-Host "429 block found: $found"
if ($found) { $c = $c.Replace($old429First, $new429First) }

# ── Add Show429Button_Click before the Helpers section ────────────────────────
$sep = [char]0x2500
$oldHelpers = "    // " + $sep + $sep + " Helpers"
$newHelpers = @"
	private void Show429Button_Click(object sender, RoutedEventArgs e)
	{
		if (string.IsNullOrEmpty(_last429Dump)) return;
		var win = new Window
		{
			Title = "Last 429 Response",
			Width = 720, Height = 520,
			Owner = this,
			WindowStartupLocation = WindowStartupLocation.CenterOwner
		};
		var tb = new System.Windows.Controls.TextBox
		{
			Text = _last429Dump,
			IsReadOnly = true,
			TextWrapping = System.Windows.TextWrapping.Wrap,
			VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
			FontFamily = new System.Windows.Media.FontFamily("Consolas"),
			FontSize = 12,
			Margin = new System.Windows.Thickness(8)
		};
		win.Content = tb;
		win.ShowDialog();
	}

	// $($sep)$($sep) Helpers
"@

$c = $c.Replace($oldHelpers, $newHelpers)

[System.IO.File]::WriteAllText($f, $c)
Write-Host "Code done"
