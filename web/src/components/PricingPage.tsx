import './PricingPage.css';

export default function PricingPage() {
  return (
    <div className="pricing-page">
      <h1>Angry Hauling — Pricing</h1>

      <section>
        <h2>Hauling Fees</h2>
        <p>Hauling fees are charged per cubic meter (m3) based on your origin system. The rate covers all costs including jump fuel, service time, and third-party transport where applicable.</p>
        <table className="pricing-table">
          <thead>
            <tr><th>Origin</th><th>Destination</th><th>Rate</th><th>What's Included</th></tr>
          </thead>
          <tbody>
            <tr>
              <td>Jita</td>
              <td>E-B957 or E-BYOS</td>
              <td>1,050 ISK/m3</td>
              <td>Third-party highsec/lowsec transport (Jita → Odebeinn) + jump freighter fuel + service</td>
            </tr>
            <tr>
              <td>Odebeinn</td>
              <td>E-B957 or E-BYOS</td>
              <td>650 ISK/m3</td>
              <td>Jump freighter fuel + service</td>
            </tr>
          </tbody>
        </table>
      </section>

      <section>
        <h2>Personal Shopper</h2>
        <p>If you'd like us to purchase items for you in Jita, we charge a flat fee to cover shopping time.</p>
        <ul>
          <li><strong>10,000,000 ISK</strong> flat shopper fee per order</li>
        </ul>
        <p>If you don't need shopping, select <strong>"Haul Only"</strong> and contract your items to the assigned hauler in the origin system.</p>
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
          <li><strong>Place your order</strong> — search for items or paste a fit/inventory list</li>
          <li><strong>A hauler accepts</strong> — they'll be assigned to your order</li>
          <li><strong>Picking up</strong> — the hauler is purchasing or collecting your items. The order can no longer be modified.</li>
          <li><strong>In transit</strong> — your items are being jump-freightered to the destination</li>
          <li><strong>Delivery</strong> — your items arrive at the destination station</li>
        </ol>
      </section>

      <section>
        <h2>Destinations</h2>
        <ul>
          <li><strong>E-B957 (Builders Edge)</strong> — Edge Dancers corp home</li>
          <li><strong>E-BYOS (The Forum)</strong> — Alliance hub</li>
        </ul>
      </section>

      <p className="pricing-back"><a href="/">← Back to Angry Hauling</a></p>
    </div>
  );
}
