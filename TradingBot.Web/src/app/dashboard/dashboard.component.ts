import { CommonModule } from '@angular/common';
import { Component, computed, signal } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatDividerModule } from '@angular/material/divider';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTableModule } from '@angular/material/table';
import { MatToolbarModule } from '@angular/material/toolbar';

type BotMode = 'monitor' | 'once' | 'paused';
type DashboardSection = 'dashboard' | 'strategy' | 'broker' | 'reports' | 'settings';

interface Candle {
  x: number;
  open: number;
  high: number;
  low: number;
  close: number;
}

interface ReportRow {
  name: string;
  type: string;
  createdAt: string;
  status: string;
}

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatCardModule,
    MatChipsModule,
    MatDividerModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MatSelectModule,
    MatSidenavModule,
    MatSlideToggleModule,
    MatTableModule,
    MatToolbarModule
  ],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss'
})
export class DashboardComponent {
  readonly activeSection = signal<DashboardSection>('dashboard');
  readonly mode = signal<BotMode>('monitor');
  readonly symbol = new FormControl('EURUSD', { nonNullable: true });
  readonly tradeOutKillZone = new FormControl(false, { nonNullable: true });

  readonly navItems: Array<{ id: DashboardSection; label: string; icon: string }> = [
    { id: 'dashboard', label: 'Dashboard', icon: 'dashboard' },
    { id: 'strategy', label: 'Strategy', icon: 'query_stats' },
    { id: 'broker', label: 'Broker', icon: 'account_balance' },
    { id: 'reports', label: 'Reports', icon: 'article' },
    { id: 'settings', label: 'Settings', icon: 'settings' }
  ];

  readonly statusCards = [
    { label: 'Broker', value: 'Connected', meta: 'cTrader Live', icon: 'hub', state: 'ok' },
    { label: 'Token', value: 'Fresh', meta: 'Expires in 30 days', icon: 'vpn_key', state: 'ok' },
    { label: 'Active Trades', value: '0 / 1', meta: 'No pending exposure', icon: 'monitoring', state: 'ok' },
    { label: 'Risk', value: '0.5%', meta: 'Per setup', icon: 'shield', state: 'warn' }
  ];

  readonly candles: Candle[] = [
    { x: 70, open: 1.1042, high: 1.1050, low: 1.1021, close: 1.1028 },
    { x: 120, open: 1.1029, high: 1.1038, low: 1.1006, close: 1.1012 },
    { x: 170, open: 1.1010, high: 1.1018, low: 1.0982, close: 1.1003 },
    { x: 220, open: 1.1002, high: 1.1035, low: 1.0998, close: 1.1031 },
    { x: 270, open: 1.1030, high: 1.1061, low: 1.1027, close: 1.1058 },
    { x: 320, open: 1.1056, high: 1.1072, low: 1.1044, close: 1.1065 },
    { x: 370, open: 1.1066, high: 1.1084, low: 1.1058, close: 1.1079 },
    { x: 420, open: 1.1078, high: 1.1090, low: 1.1069, close: 1.1072 },
    { x: 470, open: 1.1071, high: 1.1098, low: 1.1067, close: 1.1093 },
    { x: 520, open: 1.1091, high: 1.1112, low: 1.1088, close: 1.1104 },
    { x: 570, open: 1.1102, high: 1.1120, low: 1.1095, close: 1.1115 }
  ];

  readonly reports: ReportRow[] = [
    { name: 'backtest-learning-EURUSD-20260519', type: 'PDF', createdAt: '15:24', status: 'Ready' },
    { name: 'backtest-trades-20260519', type: 'CSV', createdAt: '14:37', status: 'Ready' },
    { name: 'learning-chart-EURUSD', type: 'SVG', createdAt: '14:36', status: 'Ready' }
  ];

  readonly reportColumns = ['name', 'type', 'createdAt', 'status'];

  readonly pageTitle = computed(() => {
    switch (this.activeSection()) {
      case 'strategy':
        return 'Strategy Workbench';
      case 'broker':
        return 'cTrader Connection';
      case 'reports':
        return 'Reports Library';
      case 'settings':
        return 'Configuration';
      default:
        return 'EURUSD Smart Money Desk';
    }
  });

  readonly pageSubtitle = computed(() => {
    switch (this.activeSection()) {
      case 'strategy':
        return 'Inspect setup logic, market structure, and execution levels';
      case 'broker':
        return 'Monitor account connectivity, token state, and exposure limits';
      case 'reports':
        return 'Review generated CSV, PDF, and chart learning outputs';
      case 'settings':
        return 'Adjust trading preferences before wiring backend persistence';
      default:
        return 'Live cTrader execution monitor and strategy control surface';
    }
  });

  readonly modeLabel = computed(() => {
    const mode = this.mode();
    if (mode === 'once') {
      return 'One cycle';
    }

    if (mode === 'paused') {
      return 'Paused';
    }

    return 'Continuous';
  });

  candleColor(candle: Candle): string {
    return candle.close >= candle.open ? '#26a69a' : '#ef5350';
  }

  y(price: number): number {
    const high = 1.1145;
    const low = 1.0970;
    return 560 - ((price - low) / (high - low)) * 470;
  }

  bodyY(candle: Candle): number {
    return Math.min(this.y(candle.open), this.y(candle.close));
  }

  bodyHeight(candle: Candle): number {
    return Math.max(Math.abs(this.y(candle.open) - this.y(candle.close)), 4);
  }
}
