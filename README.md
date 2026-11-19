# NEXUS CAM

A Windows desktop project exploring a lightweight virtual-camera background replacement workflow.

## Current status

This repository is an initial project foundation, **not a finished virtual camera**. It does not yet provide a Zoom-selectable camera or reliable AI background segmentation. Do not install drivers or run scripts as Administrator from this repository until the implementation and build have been reviewed.

## Target design

- Windows 10/11 x64 desktop application.
- Dark Liquid Glass-inspired interface.
- Camera preview and background image selection.
- Low-resource mode for older integrated graphics.
- Background segmentation that runs locally.
- A separately tested virtual-camera output for Zoom and other apps.

## Hardware expectations

The intended target includes older integrated GPUs. The app must offer a low-resolution / low-FPS mode and must not assume an NVIDIA GPU.

## Development

The implementation will be added in small, buildable steps. A virtual camera requires a correctly signed/installed Windows camera component; a normal desktop preview alone cannot appear as a camera device in Zoom.

## Safety

No credentials, API keys, or private tokens belong in this repository.