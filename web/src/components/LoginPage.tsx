import { useState } from 'react';
import { getLoginUrl } from '../api';
import './LoginPage.css';

export default function LoginPage() {
  const [loading, setLoading] = useState(false);

  async function handleLogin() {
    setLoading(true);
    const url = await getLoginUrl();
    window.location.href = url;
  }

  return (
    <div className="login-page">
      <div className="login-card">
        <h1>Angry Hauling</h1>
        <p className="login-subtitle">Jump Freighter Logistics for Angry Miners Alliance.</p>
        <button className="login-btn" onClick={handleLogin} disabled={loading}>
          {loading ? 'Redirecting...' : 'Login with EVE Online'}
        </button>
      </div>
    </div>
  );
}
