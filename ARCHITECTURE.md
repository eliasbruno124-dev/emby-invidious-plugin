# Architecture Documentation for Emby-Invidious-Plugin

## Overview
The Emby-Invidious-Plugin is designed to integrate Invidious, an alternative front-end to YouTube, into the Emby media server. This document outlines the architecture, main components, design patterns utilized, and the overall code structure.

## Components

### 1. **Plugin Manager**
The Plugin Manager is responsible for loading and managing plugin instances. It ensures that each instance is properly initialized and terminated, managing their lifecycle and interactions with the Emby server.

### 2. **Invidious API Client**
This component handles all communications with the Invidious instance. It is responsible for sending requests and processing responses, including error handling and retries for failed requests.

### 3. **Media Item Handlers**
These handlers are responsible for converting Invidious content into Emby-compatible media items. They handle metadata extraction and transformation, ensuring that the media items appear correctly within the Emby interface.

### 4. **User Interface Extensions**
This component enhances the Emby user interface to incorporate Invidious features. It adds new menu options, custom views for Invidious content, and other UI elements that improve user interaction.

## Design Patterns
The Emby-Invidious-Plugin employs several design patterns including:

- **Singleton**: Used for the Plugin Manager to ensure a single instance manages all plugins.
- **Factory Pattern**: Employed for creating various media item handlers based on the type of content being processed.
- **Observer Pattern**: Used for event handling, allowing UI components to update in response to changes in the underlying data model.

## Code Structure

```
Emby-Invidious-Plugin/
|-- src/
|   |-- PluginManager.cs
|   |-- InvidiousApiClient.cs
|   |-- MediaItemHandlers/
|   |   |-- VideoHandler.cs
|   |   |-- PlaylistHandler.cs
|   |-- UI/
|   |   |-- MenuExtensions.cs
|   |   |-- CustomViews/
|   |       |-- InvidiousView.cs
|-- README.md
|-- LICENSE
```  

### File Descriptions
- **PluginManager.cs**: Implements the core logic for managing the plugin's lifecycle.
- **InvidiousApiClient.cs**: Manages API calls to the Invidious service.
- **MediaItemHandlers/**: Contains handlers for different types of media.
- **UI/**: Contains modifications/extensions to the Emby user interface.

## Conclusion
This document provides a high-level overview of the Emby-Invidious-Plugin architecture. For detailed implementation notes, refer to the individual component documentation in the repository.
