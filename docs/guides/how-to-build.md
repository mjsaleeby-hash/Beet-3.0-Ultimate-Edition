# How to Build BeetsBackup.exe from Source

This guide walks you through building the BeetsBackup executable from the downloaded project files using PowerShell. No Visual Studio or VS Code required.

---

## What You Need

- **Windows 10 or later** (64-bit)
- **The .NET 8 SDK** (free download from Microsoft)

---

## Step 1: Install the .NET 8 SDK

1. Open your web browser and go to:

   **https://dotnet.microsoft.com/download/dotnet/8.0**

2. Under the **SDK** column, click the **Windows x64** installer to download it.

3. Run the installer and follow the prompts. Accept the defaults.

4. When it finishes, **close and reopen any PowerShell windows** so the new commands are available.

---

## Step 2: Download the Project

1. Go to the GitHub repository page for Beet's Backup.

2. Click the green **Code** button, then click **Download ZIP**.

3. Open your **Downloads** folder and find the ZIP file (e.g., `Beet-3.0-Ultimate-Edition-main.zip`).

4. **Right-click** the ZIP file and select **Extract All...**

5. Extract it to your Downloads folder. You should now have a folder like:

   ```
   C:\Users\Downloads\Beet-3.0-Ultimate-Edition-main
   ```

---

## Step 3: Open PowerShell

1. Press the **Windows key** on your keyboard.

2. Type **PowerShell** and click on **Windows PowerShell** to open it.

You should see a window with text like:

```
PS C:\Users\YourName>
```

---

## Step 4: Navigate to the Project Folder

Type the following command and press **Enter**:

```powershell
cd C:\Users\Downloads\Beet-3.0-Ultimate-Edition-main
```

> **Note:** If your extracted folder has a different name, adjust the path accordingly. You can check by opening File Explorer and looking at the folder name inside your Downloads folder.

To confirm you are in the right place, type:

```powershell
dir *.csproj
```

You should see `BeetsBackup.csproj` in the output. If you don't, you may need to go one folder deeper:

```powershell
cd Beet-3.0-Ultimate-Edition-main
```

Then try `dir *.csproj` again.

---

## Step 5: Build the Executable

Type the following command and press **Enter**:

```powershell
dotnet publish -c Release
```

This will download any required packages and compile the project into a single `.exe` file. The first time you run this, it may take a couple of minutes.

When it finishes, you should see a message like:

```
BeetsBackup -> C:\Users\Downloads\Beet-3.0-Ultimate-Edition-main\bin\Release\net8.0-windows\win-x64\publish\
```

---

## Step 6: Find Your Executable

When the build finishes, your `BeetsBackup.exe` file will be located here:

```
C:\Users\Downloads\Beet-3.0-Ultimate-Edition-main\bin\Release\net8.0-windows\win-x64\publish\BeetsBackup.exe
```

To find it using File Explorer:

1. Open **File Explorer** (the folder icon on your taskbar, or press **Windows key + E**).
2. In the address bar at the top, paste the following path and press **Enter**:

   ```
   C:\Users\Downloads\Beet-3.0-Ultimate-Edition-main\bin\Release\net8.0-windows\win-x64\publish
   ```

3. You should see **BeetsBackup.exe** in that folder.

A second copy is also placed one level above the project folder:

```
C:\Users\Downloads\BeetsBackup.exe
```

### Copy it to your Desktop (optional)

If you want easy access, you can copy the exe to your Desktop. Type this in PowerShell and press **Enter**:

```powershell
copy bin\Release\net8.0-windows\win-x64\publish\BeetsBackup.exe "$HOME\Desktop\"
```

Or simply **right-click** the file in File Explorer, select **Copy**, then go to your Desktop, **right-click** an empty area, and select **Paste**.

---

## Step 7: Run It

Double-click `BeetsBackup.exe` to launch the application. No installer or additional setup is needed -- everything is bundled into that single file.

---

## Troubleshooting

### "dotnet is not recognized"

The .NET SDK was not installed correctly, or PowerShell was not restarted after installation. Close PowerShell, reopen it, and try again. If it still doesn't work, reinstall the .NET 8 SDK.

### "The term 'cd' could not be found" or path errors

Make sure the folder path is correct. Open File Explorer, navigate to the extracted folder, and check the exact path shown in the address bar.

### Build errors about missing files

Make sure you extracted the full ZIP file and did not just open it without extracting. Windows lets you browse ZIP files without extracting, but the build requires all files to be fully extracted.

---

## Quick Reference (All Commands)

```powershell
cd C:\Users\Downloads\Beet-3.0-Ultimate-Edition-main
dotnet publish -c Release
copy bin\Release\net8.0-windows\win-x64\publish\BeetsBackup.exe "$HOME\Desktop\"
```
