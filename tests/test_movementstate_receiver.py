#!/usr/bin/env python3
"""
Test script to check if VHI_MovementState stream is delivering data.
"""

try:
    from pylsl import StreamInlet, resolve_byprop
    print("✅ pylsl imported successfully")
except ImportError as e:
    print("❌ pylsl not found. Install with: pip install pylsl")
    print(e)
    exit(1)

import time

def main():
    print("\n🔍 Searching for VHI_MovementState stream...")
    print("   (Press Ctrl+C to stop)\n")

    # Try to find the stream
    try:
        # Search for the stream by name with 5 second timeout
        streams = resolve_byprop('name', 'VHI_MovementState', timeout=5.0)

        if not streams:
            print("❌ VHI_MovementState stream not found")
            print("   Make sure the Godot VHI app is running with outlets enabled")
            return

        # Create inlet
        inlet = StreamInlet(streams[0])

        # Get stream info
        info = inlet.info()
        print(f"✅ Connected to: {info.name()}")
        print(f"   Type: {info.type()}")
        print(f"   Channels: {info.channel_count()}")
        print(f"   Sample rate: {info.nominal_srate()} Hz (0 = irregular)")
        print(f"   Format: {info.channel_format()}\n")
        print("Listening for movement changes...\n")
        print("Use arrow keys in VHI to change movements\n")

        sample_count = 0

        while True:
            # Pull sample (non-blocking with 0.1s timeout)
            sample, timestamp = inlet.pull_sample(timeout=0.1)

            if sample:
                sample_count += 1
                print(f"[{sample_count}] Timestamp: {timestamp:.3f}")
                print(f"    Movement: {sample[0]}")
                print()

            time.sleep(0.01)  # Small delay to avoid busy loop

    except KeyboardInterrupt:
        print("\n\n👋 Stopped by user")
    except Exception as e:
        print(f"\n❌ Error: {e}")

if __name__ == "__main__":
    main()
