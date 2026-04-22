import { useState, useEffect } from 'react';
import type { UserInfo } from './types';
import { getMe } from './api';
import LoginPage from './components/LoginPage';
import MainPage from './components/MainPage';
import './App.css';

function App() {
  const [user, setUser] = useState<UserInfo | null>(null);
  const [loading, setLoading] = useState(true);
  const [denied, setDenied] = useState('');

  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const token = params.get('token');
    const deniedName = params.get('denied');

    if (deniedName) {
      setDenied(deniedName);
      setLoading(false);
      window.history.replaceState({}, '', window.location.pathname);
      return;
    }

    if (token) {
      sessionStorage.setItem('hauling_token', token);
      window.history.replaceState({}, '', window.location.pathname);
    }

    getMe().then(u => {
      setUser(u);
      setLoading(false);
    }).catch(() => setLoading(false));
  }, []);

  function handleLogout() {
    sessionStorage.removeItem('hauling_token');
    setUser(null);
  }

  if (loading) return <div className="loading">Loading...</div>;
  if (denied) return <DeniedPage characterName={denied} onBack={() => setDenied('')} />;
  if (!user) return <LoginPage />;
  return <MainPage user={user} onLogout={handleLogout} />;
}

function DeniedPage({ characterName, onBack }: { characterName: string; onBack: () => void }) {
  return (
    <div className="denied-page">
      <div className="denied-card">
        <h1>Access Denied</h1>
        <p className="denied-char">{characterName}</p>
        <p className="denied-msg">
          Angry Hauling is available to <strong>Angry Miners Alliance.</strong> members only.
        </p>
        <p className="denied-hint">
          If you believe this is an error, make sure you're logging in with a character that is in the alliance.
        </p>
        <button className="denied-btn" onClick={onBack}>Back to Login</button>
      </div>
    </div>
  );
}

export default App;
