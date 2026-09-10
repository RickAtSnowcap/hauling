import { useState, useEffect } from 'react';
import type { ConfigResponse } from '../types';
import { getConfig } from '../api';
import './PricingPage.css';

export default function PricingPage() {
  const [config, setConfig] = useState<ConfigResponse | null>(null);
  useEffect(() => { getConfig().then(setConfig); }, []);

  function fmtIsk(n: number): string {
    return Math.round(n).toLocaleString(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 0 });
  }

  const jitaRoutes = config?.routes.filter(r => r.has_pushx) ?? [];
  const assetSafetyRoutes = config?.routes.filter(r =>
    !r.has_pushx && (r.origin === 'Odebeinn' || r.origin === 'Konora')
  ) ?? [];
  const shuttleRoutes = config?.routes.filter(r =>
    !r.has_pushx && r.origin !== 'Odebeinn' && r.origin !== 'Konora'
  ) ?? [];

  return (
    <div className="pricing-page">
      <h1>Angry Hauling — Pricing</h1>

      <p className="pricing-relocation-note">
        <strong>Pure Blind:</strong> Edge Dancers home is <strong>DK-FXK</strong>.
        The service fee is currently <strong>waived</strong>.
        {config && <> Fuel rates use live Jita Nitrogen Isotope pricing ({fmtIsk(config.isotope_price)} ISK/unit).</>}
      </p>

      <section>
        <h2>Jita Routes (via PushX + JF)</h2>
        <p>PushX handles the Jita–Nonni highsec leg (5 systems), our jump freighters handle Aunenen to destination.</p>

        <h3>PushX Courier Fees</h3>
        <table className="pricing-table">
          <thead>
            <tr><th>Volume</th><th>Standard</th><th>Rush</th></tr>
          </thead>
          <tbody>
            {jitaRoutes.length > 0 && (
              <>
                <tr>
                  <td>&le; {fmtIsk(jitaRoutes[0].pushx_volume_tier_break)} m&sup3;</td>
                  <td>{fmtIsk(jitaRoutes[0].pushx_fee_small)} ISK</td>
                  <td>{fmtIsk(jitaRoutes[0].pushx_rush_fee_small)} ISK</td>
                </tr>
                <tr>
                  <td>&gt; {fmtIsk(jitaRoutes[0].pushx_volume_tier_break)} m&sup3;</td>
                  <td>{fmtIsk(jitaRoutes[0].pushx_fee_large)} ISK</td>
                  <td>{fmtIsk(jitaRoutes[0].pushx_rush_fee_large)} ISK</td>
                </tr>
              </>
            )}
          </tbody>
        </table>

        <h3>JF Fuel per Route</h3>
        <table className="pricing-table">
          <thead>
            <tr><th>Route</th><th>Fuel Rate</th><th>RT Isotopes</th></tr>
          </thead>
          <tbody>
            {jitaRoutes.map(r => (
              <tr key={`${r.origin}-${r.destination}`}>
                <td>{r.origin} &rarr; {r.destination}</td>
                <td>{fmtIsk(r.fuel_isk_per_m3)} ISK/m&sup3;</td>
                <td>{fmtIsk(r.round_trip_isotopes)}</td>
              </tr>
            ))}
          </tbody>
        </table>
        <p><strong>Formula:</strong> <code>PushX fee + (fuel + service) &times; m&sup3;</code></p>
      </section>

      <section>
        <h2>Rush Delivery (Jita routes only)</h2>
        <p>Tick <strong>Rush Delivery</strong> to use PushX's rush service on the Jita–Nonni leg.</p>
        <ul>
          <li>Cuts the Jita &harr; Nonni leg from <strong>up to 2 days</strong> down to <strong>hours</strong></li>
          <li>The JF leg timing is unchanged — that's our team</li>
        </ul>
      </section>

      {assetSafetyRoutes.length > 0 && (
        <section>
          <h2>Asset Safety Recovery (Inbound Only)</h2>
          <p>Odebeinn and Konora trips are for asset safety recovery. No PushX leg, no shopping — just fuel.</p>
          <table className="pricing-table">
            <thead>
              <tr><th>Route</th><th>Fuel Rate</th><th>RT Isotopes</th></tr>
            </thead>
            <tbody>
              {assetSafetyRoutes.map(r => (
                <tr key={`${r.origin}-${r.destination}`}>
                  <td>{r.origin} &rarr; {r.destination}</td>
                  <td>{fmtIsk(r.fuel_isk_per_m3)} ISK/m&sup3;</td>
                  <td>{fmtIsk(r.round_trip_isotopes)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <p><strong>Formula:</strong> <code>(fuel + service) &times; m&sup3;</code></p>
          <p><em>These are long hauls — the fuel cost reflects the distance.</em></p>
        </section>
      )}

      {shuttleRoutes.length > 0 && (
        <section>
          <h2>Inter-System Shuttle</h2>
          <p>Short JF runs between our Pure Blind stations. No PushX, no shopping — just fuel.</p>
          <table className="pricing-table">
            <thead>
              <tr><th>Route</th><th>Fuel Rate</th><th>RT Isotopes</th></tr>
            </thead>
            <tbody>
              {shuttleRoutes.map(r => (
                <tr key={`${r.origin}-${r.destination}`}>
                  <td>{r.origin} &rarr; {r.destination}</td>
                  <td>{fmtIsk(r.fuel_isk_per_m3)} ISK/m&sup3;</td>
                  <td>{fmtIsk(r.round_trip_isotopes)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <p><strong>Formula:</strong> <code>(fuel + service) &times; m&sup3;</code></p>
        </section>
      )}

      <section>
        <h2>Personal Shopper (Jita-origin only)</h2>
        <p>If you'd like us to purchase items for you in Jita, we charge a flat fee to cover shopping time. Only available when Jita is the <em>origin</em>.</p>
        <ul>
          <li><strong>{config ? fmtIsk(config.shopper_fee) : '10,000,000'} ISK</strong> flat shopper fee per order</li>
        </ul>
        <p>If you don't need shopping, select <strong>"Haul Only"</strong> and contract your items to the assigned hauler at the origin.</p>
      </section>

      <section>
        <h2>Dynamic Fuel Pricing</h2>
        <p>Fuel rates update automatically based on the live Jita sell price of Nitrogen Isotopes.</p>
        <ul>
          <li><strong>Current isotope price:</strong> {config ? fmtIsk(config.isotope_price) : '...'} ISK/unit</li>
          <li><strong>Cargo capacity:</strong> {config ? fmtIsk(config.cargo_capacity) : '370,000'} m&sup3; (Rhea)</li>
          <li><strong>Formula:</strong> <code>(isotope price &times; round trip isotopes) / cargo capacity</code></li>
        </ul>
      </section>

      <section>
        <h2>Order Limits</h2>
        <ul>
          <li><strong>Maximum volume:</strong> {config ? fmtIsk(config.max_order_m3) : '370,000'} m&sup3; per order (Rhea cargo capacity)</li>
          <li><strong>Maximum cargo value:</strong> 5,000,000,000 ISK per PushX contract (Jita routes)</li>
          <li>Items must be <strong>packaged</strong> (repackaged) — assembled ships use packaged volume for pricing</li>
        </ul>
      </section>

      <section>
        <h2>How It Works</h2>
        <ol>
          <li><strong>Place your order</strong> — pick your route, then search for items or paste a fit/inventory list</li>
          <li><strong>A hauler accepts</strong> — they'll be assigned to your order</li>
          <li><strong>Picking up</strong> — the hauler is purchasing or collecting your items at the origin. The order can no longer be modified.</li>
          <li><strong>In transit</strong> — your items are being jump-freightered along the route</li>
          <li><strong>Delivery</strong> — your items arrive at the destination</li>
        </ol>
      </section>

      <p className="pricing-back"><a href="/">← Back to Angry Hauling</a></p>
    </div>
  );
}
