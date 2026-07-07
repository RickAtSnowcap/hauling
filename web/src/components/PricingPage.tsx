import './PricingPage.css';

export default function PricingPage() {
  return (
    <div className="pricing-page">
      <h1>Angry Hauling — Pricing</h1>

      <p className="pricing-relocation-note">
        <strong>The Spire:</strong> Our alliance home is the <strong>Z19-B8 Black Canary</strong> fortizar.
        The service fee is <strong>waived</strong> while we settle in.
      </p>

      <section>
        <h2>Routes</h2>
        <p>Every trip has Z19-B8 as one endpoint. We run both inbound (to alliance space) and outbound (back to highsec or asset-safety).</p>
        <ul>
          <li><strong>Jita 4-4 ↔ Z19-B8</strong> — Evola handles the Jita-Odebeinn leg, AMA jump freighters handle the Odebeinn-Z19 leg</li>
          <li><strong>Odebeinn (V-V) ↔ Z19-B8</strong> — single jump freighter leg, no Evola</li>
        </ul>
      </section>

      <section>
        <h2>Hauling Fee — Jita ↔ Z19-B8</h2>
        <p>Jita trips pass through three real costs in either direction: Evola's highsec courier (Jita ↔ Odebeinn), jump freighter fuel (Odebeinn ↔ Z19-B8 round trip), and the hauler service fee.</p>
        <table className="pricing-table">
          <thead>
            <tr><th>Component</th><th>Rate</th><th>What It Covers</th></tr>
          </thead>
          <tbody>
            <tr>
              <td>Evola transport</td>
              <td>400 ISK/m³ + 1% of cargo value<br/><em>(32.5M ISK minimum)</em></td>
              <td>Pass-through of Evola Deliveries' rate. The 1% collateral charge is what makes ships and deadspace gear cost more than ammo, m³-for-m³.</td>
            </tr>
            <tr>
              <td>Jump freighter fuel</td>
              <td>263 ISK/m³</td>
              <td>Oxygen Isotopes for the Anshar round trip Odebeinn ↔ Z19-B8 (139,142 isotopes round trip)</td>
            </tr>
            <tr>
              <td>Service fee</td>
              <td><strong>0 ISK/m³</strong> (waived)</td>
              <td>Currently waived during the Spire settle-in period. Expected to return at ~143 ISK/m³ (50M ISK per full load).</td>
            </tr>
          </tbody>
        </table>
        <p>
          <strong>Formula:</strong>&nbsp;
          <code>max(400 × m³ + 1% × cargo_value, 32.5M) + (263 + service) × m³</code>
        </p>
        <p>
          We use the live Jita sell price for cargo value &mdash; this matters in both directions because Evola charges 1% collateral on whatever it's carrying. Items not on the Jita market (abyssals, ungrouped storage) get a 0 ISK value &mdash; please tell Bendigo Xana if your order includes those so the collateral can be hand-set.
        </p>
      </section>

      <section>
        <h2>What the 1% Collateral Buys You</h2>
        <p>
          The 1% isn't a markup &mdash; it's the cost of posting <strong>full collateral equal to your cargo's value</strong> with Evola. If anything happens to your shipment between Jita and Odebeinn, Evola pays out the full cargo value from that collateral. It's effectively insurance, baked into the rate.
        </p>
        <p>
          That coverage matters because the <strong>Jita ↔ Odebeinn leg is the risky one</strong>. It runs through highsec and lowsec chokepoints where most courier losses actually happen &mdash; in either direction.
        </p>
        <p>
          The <strong>Odebeinn ↔ Z19-B8 jump freighter leg is the safer half.</strong> It's a direct jump between a known staging system and alliance space, flown by AMA pilots in well-fit Anshars. Risk exists, but in practice it's a small fraction of what the highsec/lowsec corridor sees.
        </p>
        <p>
          Short version: when you pay the 1%, you're insuring the most dangerous part of the trip. The cheaper part of the route is also the safer part.
        </p>
      </section>

      <section>
        <h2>Hauling Fee — Odebeinn ↔ Z19-B8</h2>
        <p>Odebeinn trips skip the Evola leg entirely, in either direction. You pay fuel and service, full stop.</p>
        <table className="pricing-table">
          <thead>
            <tr><th>Component</th><th>Rate</th><th>What It Covers</th></tr>
          </thead>
          <tbody>
            <tr>
              <td>Jump freighter fuel</td>
              <td>263 ISK/m³</td>
              <td>Same fuel cost as the Jita route's nullsec leg</td>
            </tr>
            <tr>
              <td>Service fee</td>
              <td><strong>0 ISK/m³</strong> (waived)</td>
              <td>Same waiver as above</td>
            </tr>
          </tbody>
        </table>
        <p><strong>Formula:</strong>&nbsp;<code>(263 + service) × m³</code></p>
      </section>

      <section>
        <h2>Personal Shopper (Jita-origin only)</h2>
        <p>If you'd like us to purchase items for you in Jita, we charge a flat fee to cover shopping time. Only available when Jita is the <em>origin</em> &mdash; we're not selling your stuff in Jita for you.</p>
        <ul>
          <li><strong>10,000,000 ISK</strong> flat shopper fee per order</li>
        </ul>
        <p>If you don't need shopping, select <strong>"Haul Only"</strong> and contract your items to the assigned hauler in the origin system.</p>
      </section>

      <section>
        <h2>Expedite (Jita ↔ Z19-B8 only)</h2>
        <p>Need it fast? Tick the <strong>Expedite</strong> checkbox to use Evola's expedited courier on the Jita-Odebeinn leg. Available in either direction on the Jita route &mdash; Odebeinn routes don't touch Evola, so there's nothing to expedite.</p>
        <ul>
          <li><strong>+150,000,000 ISK</strong> flat fee, passed through from Evola</li>
          <li>Cuts the Jita ↔ Odebeinn leg from <strong>2&ndash;3 days</strong> down to <strong>hours</strong></li>
          <li>The Odebeinn ↔ Z19-B8 jump freighter leg timing is unchanged — that's our team</li>
        </ul>
      </section>

      <section>
        <h2>Order Limits</h2>
        <ul>
          <li><strong>Maximum volume:</strong> 350,000 m3 per order (jump freighter cargo capacity)</li>
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
