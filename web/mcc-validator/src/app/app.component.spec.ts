import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { AppComponent } from './app.component';
import { routes } from './app.routes';

describe('AppComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AppComponent],
      providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()]
    }).compileComponents();
  });

  it('renders the suite navigation', () => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent as string;
    for (const label of ['Unified risk score', 'KYB & screening', 'Underwriting', 'MCC validator', 'Case queue', 'Policy rules', 'Audit trail', 'Model ops', 'MATCH inquiry', 'Webhooks']) {
      expect(text).toContain(label);
    }
  });

  it('exposes a route for every suite page', () => {
    const paths = routes.map(r => r.path);
    for (const p of ['score', 'kyb', 'underwriting', 'mcc', 'cases', 'cases/:id', 'rules', 'audit', 'models', 'match', 'webhooks']) {
      expect(paths).toContain(p);
    }
  });
});
