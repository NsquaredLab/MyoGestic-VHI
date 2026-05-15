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

def main():
    print("\n🔍 Searching for VHI streams...")
    print("   (Press Ctrl+C to stop)\n")

    inlets = {}
    stream_names = ['VHI_Control', 'VHI_Predict']

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

    counts = {name: 0 for name in inlets}
    latest = {name: None for name in inlets}
    last_print = time.time()

    try:
        while True:
            for stream_name, inlet in inlets.items():
                sample, _timestamp = inlet.pull_sample(timeout=0.0)
                if sample:
                    counts[stream_name] += 1
                    latest[stream_name] = sample

            # VHI_Control / VHI_Predict are continuous 60 Hz pose streams —
            # summarise the rate and latest pose once per second.
            if time.time() - last_print >= 1.0:
                for stream_name in inlets:
                    sample = latest[stream_name]
                    if sample is None:
                        print(f"[{stream_name}] no samples yet")
                    else:
                        pose = " ".join(f"{v:+.2f}" for v in sample[:9])
                        print(f"[{stream_name}] {counts[stream_name]:3d} Hz | {pose}")
                    counts[stream_name] = 0
                print("-" * 80)
                last_print = time.time()

            time.sleep(0.01)  # Small delay

    except KeyboardInterrupt:
        print("\n\n👋 Stopped by user")
    except Exception as e:
        print(f"\n❌ Error: {e}")

if __name__ == "__main__":
    main()
