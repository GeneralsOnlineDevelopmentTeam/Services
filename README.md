# Services
GeneralsOnline Game Services Code provides RESTful web services which act as a replacement and modernization of the GameSpy functionality found in the original release of Command & Conquer: Generals - Zero Hour

# Build Status: Windows
[![Windows (x64 + arm64)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/Windows.yml/badge.svg)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/Windows.yml)

# Build Status: Linux
[![Linux (x64 + arm64)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/Linux-MUSL.yml/badge.svg)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/Linux-MUSL.yml)
[![Linux MUSL (x64 + arm64)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/Linux.yml/badge.svg)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/Linux.yml)

# Build Status: MacOS
[![MacOS (x64 + arm64)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/MacOS.yml/badge.svg)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/MacOS.yml)

# Original Game Functionality Implemented
- Quick Match
- Custom Match
- Ladders
- Rooms & Chat
- Profiles
- Player Stats
- Friends / Buddies / Social System
- Global / Daily Stats
- Auto-update

# Required Dependencies
- Microsoft Visual Studio (2026 Community Edition recommended)
- MySQL/MariaDB server (MariaDB 12.1.2 or higher recommended)
- MySQL management tool or command line (e.g. HeidiSQL)
- .NET SDK 10

# Optional Dependencies (not required for development, required for live)
- Discord App ID
- S3 compatible storage
- STUN + TURN servers

# How To Build
- Sync the repository
- Open GenServices.sln
- Build the solution for x64, Windows (or your architecture & OS if different)
- Edit appsettings.json and fill out any TODO sections (e.g. token settings, database settings)
- Import the SQL structure to your database (GenOnlineService\Database_Structure\structure.sql)
- When upgrading an existing database, apply new `GenOnlineService\Database_Structure\upgrade_*.sql` files in date order before starting the updated service
- Run GenOnlineService.exe
