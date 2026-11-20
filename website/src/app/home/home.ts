import { Component, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTabsModule } from '@angular/material/tabs';
import { PRODUCTS } from '../content/catalog';
import { USE_CASES } from '../content/use-cases';

@Component({
  selector: 'app-home',
  imports: [RouterLink, MatButtonModule, MatIconModule, MatTabsModule],
  templateUrl: './home.html',
  styleUrl: './home.scss',
})
export class Home {
  protected readonly products = PRODUCTS;
  protected readonly stories = USE_CASES.filter((item) => item.image);
  protected readonly dataPath = [
    {
      icon: 'storage',
      title: 'Store',
      detail: 'PostgreSQL owns transactional data, constraints, and permissions.',
      href: '/provider',
    },
    {
      icon: 'terminal',
      title: 'Query',
      detail: 'ADO.NET and EF Core expose SQL, LINQ, and graph traversal.',
      href: '/ef-core',
    },
    {
      icon: 'sensors',
      title: 'Deliver',
      detail: 'Streams and Sync move committed transactions with durable progress.',
      href: '/real-time/streams',
    },
    {
      icon: 'display_settings',
      title: 'Update',
      detail: 'Live delivers authorized changes to connected applications.',
      href: '/real-time/live',
    },
  ];
  protected readonly copyMessage = signal('Copy');
  protected readonly examples = [
    {
      label: 'ADO.NET',
      file: 'Program.cs',
      code: [
        'using BlueTusk.Data;',
        '',
        'await using var db =',
        '    new BlueTuskDataSourceBuilder(connectionString).Build();',
        '',
        'await using var command = db.CreateCommand(',
        '    "SELECT $1::int4 + $2::int4");',
        'command.Parameters.Add(new BlueTuskParameter<int>(20));',
        'command.Parameters.Add(new BlueTuskParameter<int>(22));',
        '',
        'Console.WriteLine(await command.ExecuteScalarAsync<int>());',
      ].join('\n'),
    },
    {
      label: 'EF Core',
      file: 'Program.cs',
      code: [
        'builder.Services.AddDbContext<AppDbContext>(options =>',
        '    options.UseBlueTusk(connectionString));',
        '',
        '// In your application service:',
        'var orders = await db.Orders',
        '    .AsNoTracking()',
        '    .Where(order => order.CustomerId == customerId)',
        '    .OrderByDescending(order => order.CreatedAt)',
        '    .ThenBy(order => order.Id)',
        '    .Take(20)',
        '    .ToListAsync(cancellationToken);',
      ].join('\n'),
    },
  ];

  protected async copyCode(code: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(code);
      this.copyMessage.set('Copied');
    } catch {
      this.copyMessage.set('Select and copy the code');
    }
  }
}
