# Jellyfin.Plugin.GrpcFfmpeg

!!!DISCLAIMER!!!

THIS PLUGIN IS ENTIRELY VIBE CODED; I HAVE NO IDEA WHAT I AM DOING; PROCEED AT YOUR OWN RISK

!!!DISCLAIMER!!!


This plugin deploys a static `grpc-ffmpeg` binary and creates wrapper scripts to redirect `ffmpeg` and `ffprobe` calls to it.

## Local Build Environment Setup (Arch Linux on WSL2)

These instructions will guide you through setting up a local build environment on Arch Linux running under WSL2.

### Prerequisites

You will need to have the .NET 6.0 SDK installed.

### Installation

1.  **Update your system:**
    Open a terminal and run the following command to ensure your system is up-to-date:
    ```bash
    sudo pacman -Syu
    ```

2.  **Install .NET 6.0 SDK:**
    Install the .NET 6.0 SDK using `pacman`:
    ```bash
    sudo pacman -S dotnet-sdk-6.0
    ```

3.  **Verify Installation:**
    Check that the SDK was installed correctly by running:
    ```bash
    dotnet --version
    ```
    This should output a version number starting with `6.x.x`.

## Building the Plugin

Once you have the .NET 6.0 SDK installed, you can build the plugin.

1.  **Clone the repository:**
    ```bash
    git clone https://github.com/your-username/Jellyfin.Plugin.GrpcFfmpeg.git
    cd Jellyfin.Plugin.GrpcFfmpeg
    ```

2.  **Build in Release mode:**
    Run the following command to build the project:
    ```bash
    dotnet build -c Release
    ```

3.  **Find the artifacts:**
    The build artifacts will be located in the `Jellyfin.Plugin.GrpcFfmpeg/bin/Release/net6.0/` directory. This will include the plugin's DLL file and other assets, ready to be zipped for deployment.
