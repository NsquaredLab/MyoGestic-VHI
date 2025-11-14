#!/usr/bin/env python3
"""
Test script to monitor all VHI LSL streams.
"""

try:
    from pylsl import StreamInlet, resolve_byprop
    print("✅ pylsl imported successfully")
except ImportError as e:
    print("❌ pylsl not found. Install with: pip install pylsl")
    print(e)
    exit(1)

import time
import json

def main():
    print("\n🔍 Searching for VHI streams...")
    print("   (Press Ctrl+C to stop)\n")

    inlets = {}
    stream_names = ['VHI_Control', 'VHI_Predict', 'VHI_MovementState', 'VHI_MenuState']

    # Try to find all streams
    for stream_name in stream_names:
        try:
            streams = resolve_byprop('name', stream_name, timeout=2.0)
            if streams:
                inlet = StreamInlet(streams[0])
                info = inlet.info()
                inlets[stream_name] = inlet
                print(f"✅ Connected to: {info.name()} (type: {info.type()}, rate: {info.nominal_srate()} Hz)")
            else:
                print(f"⚠️  {stream_name} not found")
        except Exception as e:
            print(f"❌ Error connecting to {stream_name}: {e}")

    if not inlets:
        print("\n❌ No VHI streams found. Make sure Godot VHI app is running with outlets enabled.")
        return

    print(f"\n📡 Monitoring {len(inlets)} stream(s)...\n")
    print("=" * 80)

    last_values = {name: None for name in inlets.keys()}

    try:
        while True:
            for stream_name, inlet in inlets.items():
                sample, timestamp = inlet.pull_sample(timeout=0.0)

                if sample:
                    # Only print if value changed (for non-continuous streams)
                    if stream_name in ['VHI_MovementState', 'VHI_MenuState']:
                        if last_values[stream_name] != sample[0]:
                            print(f"\n[{stream_name}] @ {timestamp:.3f}")
                            if stream_name == 'VHI_MenuState':
                                try:
                                    menu_data = json.loads(sample[0])
                                    print(f"  Menu Settings:")
                                    for key, value in menu_data.items():
                                        print(f"    {key}: {value}")
                                except json.JSONDecodeError:
                                    print(f"  Raw: {sample[0]}")
                            else:
                                print(f"  Movement: {sample[0]}")
                            last_values[stream_name] = sample[0]

            time.sleep(0.01)  # Small delay

    except KeyboardInterrupt:
        print("\n\n👋 Stopped by user")
    except Exception as e:
        print(f"\n❌ Error: {e}")

if __name__ == "__main__":
    main()
