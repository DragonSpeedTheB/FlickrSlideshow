$f = "C:\code\FlickrSlideshow\FlickrSlideshow.XboxTest\MainWindow.xaml.cs"
$c = [System.IO.File]::ReadAllText($f)

$sep = ", "

$oldBlock = '                if (status != HttpStatusCode.TooManyRequests)' + "`r`n" +
			'                {' + "`r`n" +
			'                    _ = await resp.Content.ReadAsByteArrayAsync(token);' + "`r`n" +
			'                    _successCount += 1;' + "`r`n" +
			'                    Dispatcher.Invoke(() => lblGoodCount.Content = "Good: " + _successCount);' + "`r`n" +
			'                }' + "`r`n" +
			'                ' + "`r`n" +
			'            }' + "`r`n" +
			'            catch (OperationCanceledException) { break; }' + "`r`n" +
			'            catch (Exception ex)' + "`r`n" +
			'            {' + "`r`n" +
			'                Log($"Request error: {ex.Message}");' + "`r`n" +
			'                await Delay(1000, token);' + "`r`n" +
			'                continue;' + "`r`n" +
			'            }' + "`r`n" +
			"`r`n" +
			'            if (status == HttpStatusCode.TooManyRequests)' + "`r`n" +
			'            {' + "`r`n" +
			'                // Capture full 429 details' + "`r`n" +
			'                var sb429 = new System.Text.StringBuilder();' + "`r`n" +
			'                sb429.AppendLine($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");' + "`r`n" +
			'                sb429.AppendLine($"URL: {url}");' + "`r`n" +
			'                sb429.AppendLine();' + "`r`n" +
			'                sb429.AppendLine("=== Response Headers ===");' + "`r`n" +
			'                foreach (var kv in resp.Headers)' + "`r`n" +
			'                    sb429.AppendLine($"{kv.Key}: {string.Join("' + $sep + '", kv.Value)}");' + "`r`n" +
			'                sb429.AppendLine();' + "`r`n" +
			'                sb429.AppendLine("=== Content Headers ===");' + "`r`n" +
			'                foreach (var kv in resp.Content.Headers)' + "`r`n" +
			'                    sb429.AppendLine($"{kv.Key}: {string.Join("' + $sep + '", kv.Value)}");' + "`r`n" +
			'                sb429.AppendLine();' + "`r`n" +
			'                sb429.AppendLine("=== Body ===");' + "`r`n" +
			'                var body429 = await resp.Content.ReadAsStringAsync(token);' + "`r`n" +
			'                sb429.Append(body429.Length > 4000 ? body429[..4000] + "..." : body429);' + "`r`n" +
			'                _last429Dump = sb429.ToString();' + "`r`n" +
			'                Dispatcher.Invoke(() => Show429Button.Visibility = Visibility.Visible);' + "`r`n" +
			"`r`n" +
			'                if (!everGot429)'

Write-Host "Searching for old block..."
$found = $c.IndexOf($oldBlock)
Write-Host "Found at index: $found"

if ($found -ge 0) {
	$newBlock = '                if (status != HttpStatusCode.TooManyRequests)' + "`r`n" +
				'                {' + "`r`n" +
				'                    _ = await resp.Content.ReadAsByteArrayAsync(token);' + "`r`n" +
				'                    _successCount++;' + "`r`n" +
				'                    Dispatcher.Invoke(() => lblGoodCount.Content = "Good: " + _successCount);' + "`r`n" +
				'                }' + "`r`n" +
				'                else' + "`r`n" +
				'                {' + "`r`n" +
				'                    // Capture full 429 details while resp is still in scope' + "`r`n" +
				'                    var sb429 = new System.Text.StringBuilder();' + "`r`n" +
				'                    sb429.AppendLine($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");' + "`r`n" +
				'                    sb429.AppendLine($"URL: {url}");' + "`r`n" +
				'                    sb429.AppendLine();' + "`r`n" +
				'                    sb429.AppendLine("=== Response Headers ===");' + "`r`n" +
				'                    foreach (var kv in resp.Headers)' + "`r`n" +
				'                        sb429.AppendLine($"{kv.Key}: {string.Join("' + $sep + '", kv.Value)}");' + "`r`n" +
				'                    sb429.AppendLine();' + "`r`n" +
				'                    sb429.AppendLine("=== Content Headers ===");' + "`r`n" +
				'                    foreach (var kv in resp.Content.Headers)' + "`r`n" +
				'                        sb429.AppendLine($"{kv.Key}: {string.Join("' + $sep + '", kv.Value)}");' + "`r`n" +
				'                    sb429.AppendLine();' + "`r`n" +
				'                    sb429.AppendLine("=== Body ===");' + "`r`n" +
				'                    var body429 = await resp.Content.ReadAsStringAsync(token);' + "`r`n" +
				'                    sb429.Append(body429.Length > 4000 ? body429[..4000] + "..." : body429);' + "`r`n" +
				'                    _last429Dump = sb429.ToString();' + "`r`n" +
				'                    Dispatcher.Invoke(() => Show429Button.Visibility = Visibility.Visible);' + "`r`n" +
				'                }' + "`r`n" +
				'            }' + "`r`n" +
				'            catch (OperationCanceledException) { break; }' + "`r`n" +
				'            catch (Exception ex)' + "`r`n" +
				'            {' + "`r`n" +
				'                Log($"Request error: {ex.Message}");' + "`r`n" +
				'                await Delay(1000, token);' + "`r`n" +
				'                continue;' + "`r`n" +
				'            }' + "`r`n" +
				"`r`n" +
				'            if (status == HttpStatusCode.TooManyRequests)' + "`r`n" +
				'            {' + "`r`n" +
				'                if (!everGot429)'

	$c = $c.Replace($oldBlock, $newBlock)
	[System.IO.File]::WriteAllText($f, $c)
	Write-Host "Saved"
} else {
	# Dump the relevant section to diagnose
	$idx = $c.IndexOf("if (status != HttpStatusCode.TooManyRequests)")
	Write-Host "if-block at: $idx"
	Write-Host $c.Substring($idx, [Math]::Min(600, $c.Length - $idx))
}
