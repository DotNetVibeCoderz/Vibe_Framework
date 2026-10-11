# Rustzor

## High Performance Web Development Platform for C# Powered by Rust & WebAssembly

> Build modern web applications using C#, execute them through a Rust-powered .NET runtime, and deploy either as interpreted applications or fully compiled WebAssembly applications.

## Vision

Rustzor adalah platform pengembangan aplikasi web generasi berikutnya yang menggabungkan produktivitas C#, performa Rust, portabilitas WebAssembly, keamanan browser sandbox, dan akses penuh ke Browser APIs.

## Arsitektur

```text
C# Source
    ↓
Rustzor Compiler
    ↓
+-------------------+
| Interpreted Mode  |
| AOT WASM Mode     |
+-------------------+
    ↓
RustNetRuntime
    ↓
WebAssembly
    ↓
Browser
```

## Build Modes

### Interpreted Runtime

- Mirip Blazor WebAssembly
- IL dieksekusi oleh RustNetRuntime
- Mendukung Hot Reload
- Reflection penuh
- Cocok untuk aplikasi enterprise

### Full AOT WebAssembly

- Kompilasi penuh menjadi WebAssembly
- Startup lebih cepat
- Ukuran deployment lebih kecil
- Cocok untuk game dan PWA

## Fondasi Runtime

Menggunakan:

- Vibe_Runtime
- RustNetRuntime

Komponen:

- IL Loader
- Metadata Reader
- Execution Engine
- Garbage Collection Layer
- Reflection System

## Browser API Libraries

Namespace utama:

```csharp
Rustzor.Browser
```

### Library Utama

- Rustzor.Browser.DOM
- Rustzor.Browser.Storage
- Rustzor.Browser.Fetch
- Rustzor.Browser.Workers
- Rustzor.Browser.WebSocket
- Rustzor.Browser.WebRTC
- Rustzor.Browser.Media
- Rustzor.Browser.Audio
- Rustzor.Browser.Video
- Rustzor.Browser.WebGPU
- Rustzor.Browser.WebGL
- Rustzor.Browser.Canvas
- Rustzor.Browser.OffscreenCanvas
- Rustzor.Browser.Gamepad
- Rustzor.Browser.WebXR
- Rustzor.Browser.NFC
- Rustzor.Browser.Bluetooth
- Rustzor.Browser.Serial
- Rustzor.Browser.USB
- Rustzor.Browser.HID
- Rustzor.Browser.FileSystem
- Rustzor.Browser.Notifications
- Rustzor.Browser.Push
- Rustzor.Browser.BackgroundSync
- Rustzor.Browser.Location
- Rustzor.Browser.Sensors
- Rustzor.Browser.AI

## Framework Tambahan

### Rustzor.Game

- ECS
- Physics
- Audio
- Networking
- WebGPU

### Rustzor.Data

- IndexedDB
- SQLite WASM
- DuckDB WASM

### Rustzor.Graphics

- WebGPU
- WebGL
- Canvas

### Rustzor.AI

- LLM
- Embedding
- RAG
- Local Inference

## Sample Applications

- CRM
- ERP
- POS
- Dashboard
- Chat Application
- Video Conference
- Figma Clone
- Photoshop Clone
- GIS Platform
- CAD Viewer
- AI Assistant
- Multiplayer Game
- Digital Twin Platform

## Roadmap

### Phase 1

- Runtime Integration
- Razor Components
- Routing
- DI
- Fetch API

### Phase 2

- IndexedDB
- Service Worker
- Notifications
- File System API

### Phase 3

- WebRTC
- WebGPU
- WebXR
- Bluetooth
- USB

### Phase 4

- AOT Compiler
- WASM Optimizer
- AI Runtime
- Game Framework

### Phase 5

- Visual Studio Extension
- Rider Extension
- VSCode Extension
- Marketplace

## Goal

Menjadi platform:

> The Rust-Powered C# Web Platform

untuk membangun Web Apps, PWA, AI Applications, GIS, CAD, Game, dan Enterprise System dengan performa WebAssembly dan produktivitas C#.
