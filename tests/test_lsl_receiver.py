#!/usr/bin/env python3
"""
LSL Test Receiver for Virtual Hand Interface

This script receives data from the Godot hand visualization outlets.
It displays the data being sent by the control and predicted hands.
"""

import time

try:
    from pylsl import StreamInlet, resolve_streams
except ImportError:
    print("Error: pylsl not installed. Install with: pip install pylsl")
    exit(1)


def find_and_connect():
    """Find and connect to hand data streams."""
    print("🔍 Searching for hand data streams...")

    streams = resolve_streams(wait_time=5.0)

    if not streams:
        print("❌ No LSL streams found!")
        return None, None

    print(f"\n📡 Found {len(streams)} stream(s):")
    for stream in streams:
        print(f"   - {stream.name()} ({stream.type()}) - {stream.channel_count()} channels @ {stream.nominal_srate()} Hz")

    # Find control and predicted outlets
    control_inlet = None
    predicted_inlet = None

    for stream in streams:
        if stream.name() == "ControlHand":
            control_inlet = StreamInlet(stream)
            print("\n✅ Connected to ControlHand outlet")
        elif stream.name() == "PredictedHand":
            predicted_inlet = StreamInlet(stream)
            print("\n✅ Connected to PredictedHand outlet")

    return control_inlet, predicted_inlet


def format_hand_data(sample):
    """Format hand data for display."""
    if len(sample) < 9:
        return "Invalid data"

    return (f"Thumb: {sample[0]:.2f}/{sample[1]:.2f} | "
            f"Index: {sample[2]:.2f} | Middle: {sample[3]:.2f} | "
            f"Ring: {sample[4]:.2f} | Pinky: {sample[5]:.2f}")


def main():
    """Main loop to receive LSL data."""
    print("\n" + "="*60)
    print("LSL Hand Data Receiver")
    print("="*60 + "\n")

    # Connect to streams
    control_inlet, predicted_inlet = find_and_connect()

    if control_inlet is None and predicted_inlet is None:
        print("\n❌ No hand data streams found. Make sure Godot is running!")
        return

    print("\n📊 Receiving data... (Press Ctrl+C to stop)\n")

    control_count = 0
    predicted_count = 0
    last_print_time = time.time()

    try:
        while True:
            # Pull from control hand
            if control_inlet:
                sample, timestamp = control_inlet.pull_sample(timeout=0.0)
                if timestamp:
                    control_count += 1

                    # Print every second
                    if time.time() - last_print_time > 1.0:
                        print(f"\n🖐️  Control Hand (rate: {control_count} Hz):")
                        print(f"    {format_hand_data(sample)}")
                        control_count = 0
                        last_print_time = time.time()

            # Pull from predicted hand
            if predicted_inlet:
                sample, timestamp = predicted_inlet.pull_sample(timeout=0.0)
                if timestamp:
                    predicted_count += 1

                    # Print every second
                    if time.time() - last_print_time > 1.0:
                        print(f"\n🤖 Predicted Hand (rate: {predicted_count} Hz):")
                        print(f"    {format_hand_data(sample)}")
                        predicted_count = 0

            time.sleep(0.01)  # Small sleep to prevent busy waiting

    except KeyboardInterrupt:
        print("\n\n✅ Stopped receiving data")


if __name__ == '__main__':
    main()
