# EmailStat – Gmail Treemap Visualiser

A **C# + WinUI 3** app for **Windows 11** that visualises your Gmail inbox using a
[WinDirStat](https://github.com/windirstat/windirstat)-style squarified treemap.
Each rectangle's area is proportional to the number of emails from that sender or domain,
making it immediately obvious which contacts or mailing-lists dominate your inbox.

---

## Features

| Feature | Details |
|---------|---------|
| **Squarified treemap** | Implements the Bruls–Huizing–van Wijk squarify algorithm with minimised aspect ratios |
| **WinDirStat colour scheme** | Vivid, high-contrast palette; hovered/selected nodes are highlighted |
| **Hover tooltip** | Shows sender name and exact email count |
| **Two grouping modes** | Toggle between **domain** (e.g. `gmail.com`) and **exact address** views |
| **Adjustable fetch depth** | Set the maximum number of messages to inspect (default 5 000) |
| **OAuth 2.0** | Read-only Gmail access; token cached locally – no re-login on restart |
| **Cancellable fetch** | Cancel a long-running fetch at any time |

---

## Screenshots

> *(Screenshots will be added once the app is running on Windows 11)*

---

## Getting Started

### Prerequisites

* Windows 11 (build 22621 or later)
* [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
* [Windows App SDK 1.5](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads) (installed automatically via NuGet)
* Visual Studio 2022 with the **Windows application development** workload
  *or* the **WinUI 3** VS component

### 1 – Clone and open

```sh
git clone https://github.com/Djspaceg/email-stat.git
cd email-stat
start EmailStat.sln   # opens in Visual Studio
```

### 2 – Create Google OAuth credentials

1. Open [Google Cloud Console](https://console.cloud.google.com/).
2. Create a new project (or select an existing one).
3. Go to **APIs & Services → Library** and enable the **Gmail API**.
4. Go to **APIs & Services → Credentials → Create Credentials → OAuth client ID**.
5. Choose **Desktop application** and give it a name.
6. Download the credentials JSON file (e.g. `client_secret_…json`).

### 3 – Build and run

```sh
# From the solution root
dotnet build src/EmailStat/EmailStat.csproj -c Release -r win-x64
dotnet run   --project src/EmailStat/EmailStat.csproj -c Release -r win-x64
```

Or press **F5** in Visual Studio (select the `x64` or `x86` platform).

### 4 – Connect

1. Click **Connect to Gmail**.
2. In the setup dialog, read the instructions and click **Select credentials file**.
3. Browse to the JSON file you downloaded in step 2.
4. A browser window will open; sign in and grant read-only access.
5. The treemap populates automatically.

---

## Project Structure

```
EmailStat.sln
src/
└── EmailStat/
    ├── EmailStat.csproj          # WinUI 3 / Windows App SDK project
    ├── App.xaml / .cs            # Application entry point
    ├── MainWindow.xaml / .cs     # Shell: toolbar, treemap host, status bar
    ├── Controls/
    │   ├── TreemapControl.xaml   # Win2D-based treemap UserControl (XAML)
    │   └── TreemapControl.xaml.cs
    ├── Helpers/
    │   ├── TreemapLayoutEngine.cs  # Squarified treemap algorithm
    │   └── ColorGenerator.cs       # WinDirStat-inspired colour palette
    ├── Models/
    │   ├── EmailGroup.cs         # Aggregated sender/domain group
    │   └── TreemapNode.cs        # Layout node (bounds + colour)
    ├── Services/
    │   └── GmailService.cs       # Gmail REST API + OAuth 2.0
    └── ViewModels/
        └── MainViewModel.cs      # MVVM: commands, state, data flow
```

---

## Architecture

```
MainWindow ──x:Bind──► MainViewModel
                             │
                             ▼
                       GmailService          (Google.Apis.Gmail.v1)
                             │ IReadOnlyList<EmailGroup>
                             ▼
                       TreemapControl
                             │
                   ┌─────────┴──────────┐
                   ▼                    ▼
          TreemapLayoutEngine     ColorGenerator
          (squarify algorithm)   (HSV palette)
                   │
                   ▼
          Win2D CanvasControl
          (Direct2D rendering)
```

---

## Key dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.WindowsAppSDK 1.5` | WinUI 3 framework |
| `Microsoft.Graphics.Win2D 1.3` | Hardware-accelerated 2D rendering |
| `CommunityToolkit.Mvvm 8.2` | `[ObservableProperty]`, `[RelayCommand]` |
| `Google.Apis.Gmail.v1` | Gmail REST API client |

---

## Privacy

EmailStat requests the `gmail.readonly` OAuth scope – it can **read** your messages but
cannot send, delete, or modify them in any way.  The OAuth token is stored locally in:

```
%LOCALAPPDATA%\EmailStat\token\
```

No data is sent to any server other than Google's own APIs.

---

## License

MIT – see [LICENSE](LICENSE).
