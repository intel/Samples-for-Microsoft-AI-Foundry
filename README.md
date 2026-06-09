# Windows AI Foundry 

Welcome to the Microsoft AI Foundry on Windows Samples Repository. This repository contains C# samples for the Windows AI Foundry, a groundbreaking platform designed to empower developers with advanced AI capabilities on Windows across a broad spectrum of silicon. The Windows AI Foundry is built on three key pillars: Built-in Models, Ready-to-use Open Source Models, and Bring Your Own Model and this repo features samples for each category running on the Intel AI PC and Copilot+ PC's.

More information on Windows AI Foundry is available at: https://learn.microsoft.com/en-us/windows/ai/overview

## Repository Contents

   - [Windows AI APIs (Built-in Models)](https://github.com/intel/Samples-for-Microsoft-AI-Foundry/tree/main/WindowsAI-Apis)
      - Phi silica  
      - Text Recognition
      - Imaging: Image Super Resolution, Image Erase, Image Extraction

   -  [Ready to Use Open Source Models Via Foundry Local](https://github.com/intel/Samples-for-Microsoft-AI-Foundry/tree/main/FoundryLocalApp)
      - Application using Foundry Local for Inferencing

   - [Bring Your Own Model(BYOM)](https://github.com/intel/Samples-for-Microsoft-AI-Foundry/tree/main/WinML/Clip-VIT)
      - Windows ML using Resnet50 
      - Windows ML using clip-vit-base-patch32 

   - [Microsoft AI Foundry App](https://github.com/intel/Samples-for-Microsoft-AI-Foundry/tree/main/MicrosoftAIFoundryApp)
      - This application demonstrates the complete Microsoft Foundry on Windows stack through a real-world technician assistant use case. It showcases how developers can integrate all three pillars of Microsoft Foundry on Windows: Windows AI APIs, Foundry Local, and Windows ML Inferencing APIs in a single, cohesive application.


   Please note that there is also a Cloud to Client Migration Recipe detailing the key considerations when migrating workloads from cloud to client available at [Cloud to Client](https://github.com/intel/Samples-for-Microsoft-AI-Foundry/tree/main/cloudtoclientmigration)

## Software Pre-reqs
1. Install latest NPU drivers by downloading it from : https://www.intel.com/content/www/us/en/download/794734/intel-npu-driver-windows.html
2. Install latest Intel GPU Drivers using Intel® Driver & Support Assistant (Intel® DSA) available at : https://www.intel.com/content/www/us/en/support/detect.html
3. Install Visual Studio 2022 Community edition or higher with C++: Visual Studio 2022, selecting  ".Net desktop Development" and “Desktop Development with C++” workloads. 
4. Clone the samples repo by doing a `git clone https://github.com/intel/Samples-for-Microsoft-AI-Foundry.git`

Please note that each of the categories of samples might have additional software prereqs which will be listed in the readme for that category.

## Hardware
- Intel® Core™ Ultra Processor Family

##  Getting help

If you have any questions or if you found any problems with this repository, please report through [GitHub issues](https://github.com/intel/Microsoft-Build2025-Samples/issues).

