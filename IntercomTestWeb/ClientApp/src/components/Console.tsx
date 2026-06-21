import { useStore } from '../store';
import { Toolbar } from './Toolbar';
import { SimDeviceCard } from './SimDeviceCard';
import { RealDeviceCard } from './RealDeviceCard';

export function Console() {
  const { sim, real } = useStore();

  const simList = Object.values(sim);
  // Mirror the WPF app, which only shows a real device once its configuration has arrived.
  const realList = Object.values(real).filter((device) => device.config);

  return (
    <div className="console">
      <Toolbar />
      <div className="device-lists">
        {simList.length === 0 && realList.length === 0 && (
          <p className="empty">
            No devices yet. Add a simulated device, or wait for real devices to appear on the bus.
          </p>
        )}
        {simList.map((device) => (
          <SimDeviceCard key={device.id} device={device} />
        ))}
        {realList.map((device) => (
          <RealDeviceCard key={device.id} device={device} />
        ))}
      </div>
    </div>
  );
}
