import { useState } from 'react';
import type { UserInfo } from '../types';
import NewOrder from './NewOrder';
import OrderList from './OrderList';
import './MainPage.css';

interface Props {
  user: UserInfo;
  onLogout: () => void;
}

export default function MainPage({ user, onLogout }: Props) {
  const [tab, setTab] = useState<'new' | 'orders'>('new');

  const isPrivileged = user.role === 'jf_pilot' || user.role === 'admin';

  return (
    <div className="main-page">
      <header className="top-bar">
        <div className="top-left">
          <h1>Angry Hauling</h1>
        </div>
        <div className="top-right">
          <span className="char-name">{user.character_name}</span>
          <span className={`role-badge role-${user.role}`}>{user.role.replace('_', ' ')}</span>
          <button className="logout-btn" onClick={onLogout}>Logout</button>
        </div>
      </header>
      <nav className="tab-bar">
        <button className={tab === 'new' ? 'active' : ''} onClick={() => setTab('new')}>New Order</button>
        <button className={tab === 'orders' ? 'active' : ''} onClick={() => setTab('orders')}>
          {isPrivileged ? 'All Orders' : 'My Orders'}
        </button>
      </nav>
      <div className="tab-content">
        {tab === 'new' && <NewOrder />}
        {tab === 'orders' && <OrderList user={user} />}
      </div>
    </div>
  );
}
